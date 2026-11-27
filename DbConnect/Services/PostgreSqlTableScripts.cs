using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class PostgreSqlSchemaProvider : ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    public Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default) =>
        PostgreSqlTableScripts.PlanDuplicateAsync(connectionString, table, newName, includeData, cancellationToken);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        PostgreSqlTableScripts.LoadAsync(connectionString, table, cancellationToken);

    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        PostgreSqlTableDesignReader.LoadAsync(connectionString, table, cancellationToken);
}

/// <summary>
/// PostgreSQL has no stored CREATE TABLE text, so the definition is assembled from the
/// system catalogs, using the server's own deparsers (format_type, pg_get_expr,
/// pg_get_constraintdef, pg_get_indexdef, pg_get_triggerdef) for every expression.
/// </summary>
internal static partial class PostgreSqlTableScripts
{
    internal static string Quote(string name) => ProviderCapabilityDeclarations.PostgreSql.QuoteIdentifier(name);
    internal static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static async Task<TableDefinitionSnapshot> LoadAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken ct)
    {
        await using var connection = PostgreSqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await TableContext.LoadAsync(connection, table, ct);
            return await new PostgreSqlTableScriptBuilder(connection, context, ct).BuildAsync();
        }
        catch (PostgresException ex)
        {
            throw new TableDefinitionUnavailableException(
                $"Could not read the definition of {table.Name}: {ex.MessageText}", ex);
        }
    }

    /// <summary>
    /// CREATE TABLE ... (LIKE ... INCLUDING ALL) copies defaults, identity, generated columns, constraints other
    /// than foreign keys, indexes, comments, storage and statistics; data is copied with OVERRIDING SYSTEM VALUE
    /// and identity sequences are moved past the copied values.
    /// </summary>
    public static async Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table,
        string newName, bool includeData, CancellationToken ct)
    {
        await using var connection = PostgreSqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await TableContext.LoadAsync(connection, table, ct);
            var target = Quote(context.Schema) + "." + Quote(newName);
            var statements = new List<string> { $"CREATE TABLE {target} (LIKE {context.Qualified} INCLUDING ALL)" };
            var notCopied = new List<string> { "Foreign keys, triggers, ownership and privileges." };
            if (context.Kind == 'p') notCopied.Add("Partitioning; the copy is an ordinary table.");
            if (includeData)
            {
                var columns = new List<string>();
                var identities = new List<string>();
                await using (var cmd = Command(connection, """
                    SELECT attname, attidentity::text FROM pg_attribute
                    WHERE attrelid = $1 AND attnum > 0 AND NOT attisdropped AND attgenerated = '' ORDER BY attnum
                    """, context.Oid))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct))
                    {
                        columns.Add(reader.GetString(0));
                        if (reader.GetString(1).Length > 0) identities.Add(reader.GetString(0));
                    }
                statements.Add(DefinitionNames.CopyRows(Quote, context.Qualified, target, columns,
                    identities.Count > 0 ? "OVERRIDING SYSTEM VALUE" : null));
                statements.AddRange(identities.Select(c =>
                    $"SELECT setval(pg_get_serial_sequence({Literal(target)}, {Literal(c)}), " +
                    $"COALESCE((SELECT max({Quote(c)}) FROM {target}), 0) + 1, false)"));
            }
            return new TableDuplicatePlan(table with { Schema = context.Schema, Name = newName }, statements, notCopied);
        }
        catch (PostgresException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.MessageText}", ex);
        }
    }

    internal static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var value in values)
            cmd.Parameters.Add(value is uint
                ? new NpgsqlParameter { Value = value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid }
                : new NpgsqlParameter { Value = value });
        return cmd;
    }

    internal sealed record TableContext(uint Oid, string Schema, string Name, char Persistence, char Kind,
        string[]? Options, string? AccessMethod, string? Tablespace, bool RowSecurity, bool ForceRowSecurity,
        char ReplicaIdentity, string? PartitionKey, string? Comment)
    {
        public string Qualified => Quote(Schema) + "." + Quote(Name);
        public bool Temporary => Persistence == 't';

        /// <summary>A partition, an inheriting table or a typed table; the script refuses these.</summary>
        public bool IsPartition { get; init; }
        public bool Inherits { get; init; }
        public bool IsTyped { get; init; }

        /// <param name="scriptable">Refuse tables the script cannot reproduce; the designer loads them and lists them as unrepresented.</param>
        public static async Task<TableContext> LoadAsync(NpgsqlConnection connection, DatabaseObjectIdentity table,
            CancellationToken ct, bool scriptable = true)
        {
            await using var cmd = Command(connection, """
                SELECT c.oid, n.nspname, c.relname, c.relpersistence::text, c.relkind::text, c.reloptions,
                       CASE WHEN am.amname <> 'heap' THEN am.amname END, ts.spcname,
                       c.relrowsecurity, c.relforcerowsecurity, c.relreplident::text,
                       CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END,
                       obj_description(c.oid, 'pg_class'), c.relispartition,
                       EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = c.oid), c.reloftype <> 0
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_am am ON am.oid = c.relam
                LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace
                WHERE n.nspname = COALESCE($1, current_schema()) AND c.relname = $2 AND c.relkind IN ('r', 'p')
                """, (object?)table.Schema ?? DBNull.Value, table.Name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new TableDefinitionUnavailableException($"Table {table.Schema}.{table.Name} was not found.");
            var name = reader.GetString(2);
            if (scriptable && reader.GetBoolean(13))
                throw new TableDefinitionUnavailableException(
                    $"{name} is a partition. Script its partitioned parent; partitions are not scripted on their own.");
            if (scriptable && reader.GetBoolean(14))
                throw new TableDefinitionUnavailableException(
                    $"{name} inherits from another table. Inherited tables cannot be scripted faithfully yet.");
            if (scriptable && reader.GetBoolean(15))
                throw new TableDefinitionUnavailableException(
                    $"{name} is a typed table (CREATE TABLE OF). Typed tables cannot be scripted faithfully yet.");
            return new TableContext(reader.GetFieldValue<uint>(0), reader.GetString(1), name, reader.GetString(3)[0],
                reader.GetString(4)[0], reader.IsDBNull(5) ? null : reader.GetFieldValue<string[]>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetBoolean(8), reader.GetBoolean(9), reader.GetString(10)[0],
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12))
            {
                IsPartition = reader.GetBoolean(13),
                Inherits = reader.GetBoolean(14),
                IsTyped = reader.GetBoolean(15)
            };
        }
    }
}
