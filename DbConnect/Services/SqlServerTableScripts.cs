using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class SqlServerSchemaProvider : ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    public Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default) =>
        SqlServerTableScripts.PlanDuplicateAsync(WithCatalog(connectionString, table.Catalog), table, newName, includeData,
            cancellationToken);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        SqlServerTableScripts.LoadAsync(WithCatalog(connectionString, table.Catalog), table, cancellationToken);

    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        SqlServerTableDesignReader.LoadAsync(WithCatalog(connectionString, table.Catalog), table, cancellationToken);
}

/// <summary>
/// SQL Server has no stored CREATE TABLE text; the definition is assembled from the sys
/// catalog views. Constraint names the server generated are not scripted, so recreating
/// generates new ones instead of colliding.
/// </summary>
internal static partial class SqlServerTableScripts
{
    internal static string Quote(string name) => ProviderCapabilityDeclarations.SqlServer.QuoteIdentifier(name);
    internal static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    public static async Task<TableDefinitionSnapshot> LoadAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await LoadContextAsync(connection, table, ct);
            return await new SqlServerTableScriptBuilder(connection, context, ct).BuildAsync();
        }
        catch (SqlException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the same definition under the new name. Constraint names are schema-scoped in SQL Server, so
    /// named ones get the new table name as a suffix. Foreign keys and triggers are not copied.
    /// </summary>
    public static async Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table,
        string newName, bool includeData, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await LoadContextAsync(connection, table, ct);
            var copy = context with { Name = newName, IsDuplicate = true };
            var snapshot = await new SqlServerTableScriptBuilder(connection, copy, ct).BuildAsync();
            var statements = snapshot.Ordered.Select(p => p.Sql).ToList();
            if (includeData)
            {
                var columns = new List<string>();
                var identity = false;
                await using (var cmd = Command(connection, """
                    SELECT c.name, c.is_identity FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                    WHERE c.object_id = @id AND c.is_computed = 0 AND t.name <> 'timestamp' ORDER BY c.column_id
                    """, context.ObjectId))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct))
                    {
                        columns.Add(reader.GetString(0));
                        identity |= reader.GetBoolean(1);
                    }
                var insert = DefinitionNames.CopyRows(Quote, context.Qualified, copy.Qualified, columns);
                statements.Add(identity
                    ? $"SET IDENTITY_INSERT {copy.Qualified} ON;\n{insert};\nSET IDENTITY_INSERT {copy.Qualified} OFF"
                    : insert);
            }
            return new TableDuplicatePlan(copy.Identity, statements, ["Foreign keys and triggers."]);
        }
        catch (SqlException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.Message}", ex);
        }
    }

    internal sealed record TableContext(int ObjectId, string Catalog, string Schema, string Name, string DefaultCollation,
        string? FileGroup, string? Compression, string LockEscalation)
    {
        /// <summary>Building a copy: suffix named constraints and leave out foreign keys and triggers.</summary>
        public bool IsDuplicate { get; init; }

        /// <summary>Table features the script refuses, e.g. "memory-optimized"; only set when loaded for the designer.</summary>
        public IReadOnlyList<string> Unscriptable { get; init; } = [];

        public string Qualified => Quote(Schema) + "." + Quote(Name);
        public DatabaseObjectIdentity Identity => new(Catalog, Schema, Name, SchemaNodeType.Table);
    }

    internal static SqlCommand Command(SqlConnection connection, string sql, int objectId)
    {
        var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", objectId);
        return cmd;
    }

    /// <param name="scriptable">Refuse tables the script cannot reproduce; the designer loads them and lists them as unrepresented.</param>
    internal static async Task<TableContext> LoadContextAsync(SqlConnection connection, DatabaseObjectIdentity table,
        CancellationToken ct, bool scriptable = true)
    {
        await using var cmd = new SqlCommand("""
            SELECT t.object_id, DB_NAME(), s.name, t.name, CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
                   CASE WHEN ds.type = 'FG' AND ds.is_default = 0 THEN ds.name END,
                   (SELECT TOP 1 p.data_compression_desc FROM sys.partitions p
                    WHERE p.object_id = t.object_id AND p.index_id IN (0, 1) AND p.data_compression <> 0),
                   t.lock_escalation_desc, t.temporal_type, t.is_memory_optimized, t.is_node, t.is_edge, t.is_filetable,
                   ds.type, t.is_external
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE s.name = @schema AND t.name = @name
            """, connection);
        cmd.Parameters.AddWithValue("@schema", table.Schema ?? "dbo");
        cmd.Parameters.AddWithValue("@name", table.Name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new TableDefinitionUnavailableException($"Table {table.Schema ?? "dbo"}.{table.Name} was not found.");
        var name = reader.GetString(3);
        var blocked = new List<string>();
        if (reader.GetByte(8) != 0) blocked.Add("system-versioned (temporal)");
        if (reader.GetBoolean(9)) blocked.Add("memory-optimized");
        if (reader.GetBoolean(10) || reader.GetBoolean(11)) blocked.Add("a graph table");
        if (reader.GetBoolean(12)) blocked.Add("a FileTable");
        if (!reader.IsDBNull(13) && reader.GetString(13).Trim() == "PS") blocked.Add("partitioned");
        if (reader.GetBoolean(14)) blocked.Add("an external table");
        if (scriptable && blocked.Count > 0)
            throw new TableDefinitionUnavailableException(
                $"{name} is {string.Join(" and ", blocked)}; DbConnect cannot script such tables faithfully yet.");
        return new TableContext(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), name, reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7))
        { Unscriptable = blocked };
    }

    /// <summary>Renders a column's declared type from sys.columns sizes.</summary>
    internal static string TypeName(string type, string typeSchema, bool userDefined, short maxLength, byte precision, byte scale)
    {
        if (userDefined) return Quote(typeSchema) + "." + Quote(type);
        return type switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString())})",
            "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{type}({precision}, {scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
            "float" => $"float({precision})",
            _ => type
        };
    }

    internal static string Columns(IEnumerable<(string Name, bool Descending)> columns) =>
        string.Join(", ", columns.Select(c => Quote(c.Name) + (c.Descending ? " DESC" : " ASC")));
}
