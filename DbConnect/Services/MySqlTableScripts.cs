using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class MySqlSchemaProvider : ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    public Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default) =>
        MySqlTableScripts.PlanDuplicateAsync(connectionString, table, newName, includeData, cancellationToken);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        MySqlTableScripts.LoadAsync(connectionString, table, cancellationToken);

    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        MySqlTableDesignReader.LoadAsync(connectionString, table, cancellationToken);
}

/// <summary>
/// SHOW CREATE TABLE is MySQL's complete table definition: columns, keys, indexes,
/// foreign keys, checks, comments, partitioning and table options. Triggers are not part
/// of it and are added from INFORMATION_SCHEMA.TRIGGERS without their DEFINER.
/// </summary>
internal static class MySqlTableScripts
{
    private static string Quote(string name) => ProviderCapabilityDeclarations.MySql.QuoteIdentifier(name);

    public static async Task<TableDefinitionSnapshot> LoadAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken ct)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        var database = table.Catalog ?? table.Schema ?? connection.Database;
        try
        {
            return await LoadCoreAsync(connection, database, table, ct);
        }
        catch (MySqlException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.Message}", ex);
        }
    }

    /// <summary>CREATE TABLE ... LIKE copies columns, keys, indexes and options, as Navicat's Duplicate Table does.</summary>
    public static async Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table,
        string newName, bool includeData, CancellationToken ct)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        var database = table.Catalog ?? table.Schema ?? connection.Database;
        var source = Quote(database) + "." + Quote(table.Name);
        var target = Quote(database) + "." + Quote(newName);
        var statements = new List<string> { $"CREATE TABLE {target} LIKE {source}" };
        if (includeData)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t AND EXTRA NOT LIKE '%GENERATED%' ORDER BY ORDINAL_POSITION
                """;
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@t", table.Name);
            var columns = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
            if (columns.Count == 0) throw new TableDefinitionUnavailableException($"Table {database}.{table.Name} was not found.");
            statements.Add(DefinitionNames.CopyRows(Quote, source, target, columns));
        }
        return new TableDuplicatePlan(table with { Catalog = database, Schema = null, Name = newName }, statements,
            ["Foreign keys and triggers."]);
    }

    private static async Task<TableDefinitionSnapshot> LoadCoreAsync(MySqlConnection connection, string database,
        DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table.Name);
        if (await cmd.ExecuteScalarAsync(ct) is not string type || type != "BASE TABLE")
            throw new TableDefinitionUnavailableException($"Table {database}.{table.Name} was not found.");

        cmd.CommandText = $"SHOW CREATE TABLE {Quote(database)}.{Quote(table.Name)}";
        string createSql;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.FieldCount < 2 || reader.IsDBNull(1))
                throw new TableDefinitionUnavailableException($"MySQL returned no definition for {table.Name}.");
            createSql = reader.GetString(1);
        }
        var parts = new List<TableDefinitionPart> { new(TableDefinitionPartKind.Table, table.Name, createSql) };

        var prerequisites = new List<TablePrerequisite>();
        cmd.CommandText = """
            SELECT DISTINCT REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t AND REFERENCED_TABLE_NAME IS NOT NULL
              AND NOT (REFERENCED_TABLE_SCHEMA = @db AND REFERENCED_TABLE_NAME = @t)
            ORDER BY 1, 2
            """;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var name = (schema == database ? "" : Quote(schema) + ".") + Quote(reader.GetString(1));
                prerequisites.Add(new($"Referenced table {name} (foreign key target)",
                    new DatabaseObjectIdentity(schema, null, reader.GetString(1), SchemaNodeType.Table)));
            }

        var exclusions = new List<string> { "Table data." };
        cmd.CommandText = """
            SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_STATEMENT FROM INFORMATION_SCHEMA.TRIGGERS
            WHERE EVENT_OBJECT_SCHEMA = @db AND EVENT_OBJECT_TABLE = @t
            ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                parts.Add(new(TableDefinitionPartKind.Trigger, name,
                    $"CREATE TRIGGER {Quote(name)} {reader.GetString(1)} {reader.GetString(2)} ON {Quote(table.Name)} " +
                    $"FOR EACH ROW {reader.GetString(3)}"));
            }
        if (parts.Count > 1)
            exclusions.Add("Trigger DEFINER and SQL mode; triggers run as the user who executes this script.");

        return new TableDefinitionSnapshot(table with { Catalog = database, Schema = null }, "MySQL", parts)
        {
            Prerequisites = prerequisites,
            Exclusions = exclusions,
            ScriptRules = ProviderCapabilityDeclarations.MySql.ScriptRules
        };
    }
}
