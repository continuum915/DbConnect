using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

internal sealed partial class SqliteSchemaProvider : ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        SqliteTableDesignReader.LoadAsync(connectionString, table, cancellationToken);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        SqliteTableScripts.LoadAsync(connectionString, table, cancellationToken);

    public Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default) =>
        SqliteTableScripts.PlanDuplicateAsync(connectionString, table, newName, includeData, cancellationToken);
}

/// <summary>
/// SQLite stores the exact CREATE statement of every table, index and trigger, so the
/// script is the stored SQL. Indexes without stored SQL back a PRIMARY KEY or UNIQUE
/// constraint and are recreated by the table statement itself.
/// </summary>
internal static class SqliteTableScripts
{
    private static string Quote(string name) => ProviderCapabilityDeclarations.Sqlite.QuoteIdentifier(name);

    public static async Task<TableDefinitionSnapshot> LoadAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken ct)
    {
        var schema = table.Schema ?? "main";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = new List<(string Type, string Name, string Sql)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT type, name, sql FROM {Quote(schema)}.sqlite_master " +
                              "WHERE tbl_name = @name COLLATE NOCASE AND sql IS NOT NULL ORDER BY rowid;";
            cmd.Parameters.AddWithValue("@name", table.Name);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            catch (SqliteException ex)
            {
                throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.Message}", ex);
            }
        }

        var tableRow = rows.FirstOrDefault(r => r.Type == "table" && r.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        if (tableRow.Sql is null)
            throw new TableDefinitionUnavailableException($"Table {table.Name} has no stored definition in {schema}.");

        var routing = Routing(schema);
        var parts = new List<TableDefinitionPart>
        {
            new(TableDefinitionPartKind.Table, tableRow.Name, routing.Table(tableRow.Sql))
        };
        parts.AddRange(rows.Where(r => r.Type == "index")
            .Select(r => new TableDefinitionPart(TableDefinitionPartKind.Index, r.Name, routing.Index(r.Sql))));
        parts.AddRange(rows.Where(r => r.Type == "trigger")
            .Select(r => new TableDefinitionPart(TableDefinitionPartKind.Trigger, r.Name, routing.Trigger(r.Sql))));

        var prerequisites = new List<TablePrerequisite>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA {Quote(schema)}.foreign_key_list({SqlLiteral(tableRow.Name)});";
            var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var target = reader.GetString(2);
                if (!target.Equals(tableRow.Name, StringComparison.OrdinalIgnoreCase)) referenced.Add(target);
            }
            prerequisites.AddRange(referenced.Select(t => new TablePrerequisite($"Referenced table {Quote(t)} (foreign key target)",
                new DatabaseObjectIdentity(null, schema, t, SchemaNodeType.Table))));
        }

        var exclusions = new List<string> { "Table data." };
        if (SqliteDefinitionSql.HasAutoincrement(tableRow.Sql))
            exclusions.Add("The AUTOINCREMENT counter in sqlite_sequence; the new table starts from 1.");
        if (IsVirtual(tableRow.Sql))
            prerequisites.Add("The virtual table module must be loaded in the target connection.");
        if (parts.Any(p => p.Kind == TableDefinitionPartKind.Trigger))
            exclusions.Add("Objects that trigger bodies reference are not scripted; they must exist when the triggers fire.");

        return new TableDefinitionSnapshot(table, "SQLite", parts)
        {
            Prerequisites = prerequisites,
            Exclusions = exclusions
        };
    }

    /// <summary>Copies the stored table and index SQL under new names; triggers are not copied.</summary>
    public static async Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table,
        string newName, bool includeData, CancellationToken ct)
    {
        var snapshot = await LoadAsync(connectionString, table, ct);
        var create = snapshot.Parts.First(p => p.Kind == TableDefinitionPartKind.Table).Sql;
        if (IsVirtual(create)) throw new TableDefinitionUnavailableException("Virtual tables cannot be duplicated.");
        var schema = table.Schema ?? "main";
        var owner = schema.Equals("main", StringComparison.OrdinalIgnoreCase) ? "" : Quote(schema) + ".";
        var statements = new List<string> { DefinitionNames.RenameTable(create, Quote(newName)) };
        statements.AddRange(snapshot.Parts.Where(p => p.Kind == TableDefinitionPartKind.Index).Select(p =>
            DefinitionNames.RenameIndex(p.Sql, Quote(DefinitionNames.Suffixed(p.Name, newName, 128)), Quote(newName))));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        if (includeData)
        {
            var columns = new List<string>();
            cmd.CommandText = $"PRAGMA {Quote(schema)}.table_xinfo({SqlLiteral(table.Name)});";
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    if (reader.GetInt32(6) == 0) columns.Add(reader.GetString(1));
            statements.Add(DefinitionNames.CopyRows(Quote, owner + Quote(table.Name), owner + Quote(newName), columns));
        }
        var notCopied = new List<string>();
        if (snapshot.Parts.Any(p => p.Kind == TableDefinitionPartKind.Trigger)) notCopied.Add("Triggers.");
        cmd.CommandText = $"PRAGMA {Quote(schema)}.foreign_key_list({SqlLiteral(table.Name)});";
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (reader.GetString(2).Equals(table.Name, StringComparison.OrdinalIgnoreCase))
                {
                    notCopied.Add("Self-referencing foreign keys still reference the original table.");
                    break;
                }
        return new TableDuplicatePlan(table with { Name = newName }, statements, notCopied);
    }

    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool IsVirtual(string sql) =>
        SqliteDefinitionSql.Tokens(sql).TakeWhile(t => !t.Is("(")).Any(t => t.Is("VIRTUAL"));

    private sealed record SchemaRouting(Func<string, string> Table, Func<string, string> Index, Func<string, string> Trigger);

    /// <summary>
    /// Stored SQL never names its schema. main needs nothing; temp needs TEMP so the
    /// table lands in temp again; an attached database qualifies each created name.
    /// </summary>
    private static SchemaRouting Routing(string schema)
    {
        if (schema.Equals("main", StringComparison.OrdinalIgnoreCase))
            return new(s => s, s => s, s => s);
        if (schema.Equals("temp", StringComparison.OrdinalIgnoreCase))
            return new(MakeTemporary, s => s, s => s);
        var qualifier = Quote(schema) + ".";
        return new(
            sql => QualifyName(sql, Header(sql, "("), qualifier),
            sql => QualifyName(sql, Header(sql, "ON"), qualifier),
            sql => QualifyName(sql, TriggerName(sql), qualifier));
    }

    private static string MakeTemporary(string sql)
    {
        var tokens = SqliteDefinitionSql.Tokens(sql);
        if (tokens.Count > 1 && (tokens[1].Is("TEMP") || tokens[1].Is("TEMPORARY"))) return sql;
        return sql[..tokens[0].End] + " TEMP" + sql[tokens[0].End..];
    }

    private static SqliteDefinitionSql.Token Header(string sql, string terminator) =>
        SqliteDefinitionSql.Tokens(sql).TakeWhile(t => !t.Is(terminator) && !t.Is("AS") && !t.Is("USING")).Last();

    private static SqliteDefinitionSql.Token TriggerName(string sql)
    {
        var tokens = SqliteDefinitionSql.Tokens(sql);
        var index = tokens.FindIndex(t => t.Is("TRIGGER")) + 1;
        if (tokens[index].Is("IF")) index += 3;
        return tokens[index];
    }

    private static string QualifyName(string sql, SqliteDefinitionSql.Token name, string qualifier) =>
        sql[..name.Start] + qualifier + sql[name.Start..];
}
