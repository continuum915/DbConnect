using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class DuckDbSchemaProvider : ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        DuckDbTableDesignReader.LoadAsync(connectionString, table, cancellationToken);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        DuckDbTableScripts.LoadAsync(connectionString, table, cancellationToken);

    public async Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table,
        string newName, bool includeData, CancellationToken cancellationToken = default)
    {
        var snapshot = await DuckDbTableScripts.LoadAsync(connectionString, table, cancellationToken);
        return DuckDbTableScripts.PlanDuplicate(snapshot, table, newName, includeData);
    }
}

/// <summary>
/// DuckDB keeps a CREATE TABLE statement per table and per index. Its stored table SQL
/// renders foreign-key targets unquoted, so those clauses are rebuilt from structured
/// constraint metadata; comments are added as COMMENT ON statements.
/// </summary>
internal static partial class DuckDbTableScripts
{
    private static string Quote(string name) => ProviderCapabilityDeclarations.DuckDb.QuoteIdentifier(name);
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static async Task<TableDefinitionSnapshot> LoadAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken ct)
    {
        var schema = table.Schema ?? "main";
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            return await LoadCoreAsync(connection, table, schema, ct);
        }
        catch (DuckDBException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the definition of {table.Name}: {ex.Message}", ex);
        }
    }

    private static async Task<TableDefinitionSnapshot> LoadCoreAsync(DuckDBConnection connection, DatabaseObjectIdentity table,
        string schema, CancellationToken ct)
    {
        string? createSql = null, comment = null;
        var catalog = table.Catalog;
        await using (var cmd = Command(connection, """
            SELECT sql, comment, database_name FROM duckdb_tables()
            WHERE database_name = COALESCE(?, current_database()) AND schema_name = ? AND table_name = ?
            """, catalog, schema, table.Name))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                createSql = reader.IsDBNull(0) ? null : reader.GetString(0);
                comment = reader.IsDBNull(1) ? null : reader.GetString(1);
                catalog = reader.GetString(2);
            }
        if (createSql is null)
            throw new TableDefinitionUnavailableException($"Table {schema}.{table.Name} was not found or has no stored definition.");

        var qualified = Quote(schema) + "." + Quote(table.Name);
        var prerequisites = new List<TablePrerequisite>();
        createSql = await RepairForeignKeysAsync(connection, catalog!, schema, table.Name, createSql, prerequisites, ct);

        var parts = new List<TableDefinitionPart> { new(TableDefinitionPartKind.Table, table.Name, createSql) };
        await using (var cmd = Command(connection, """
            SELECT index_name, sql, comment FROM duckdb_indexes()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? ORDER BY index_oid
            """, catalog, schema, table.Name))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1))
                    throw new TableDefinitionUnavailableException($"Index {reader.GetString(0)} has no stored definition.");
                parts.Add(new(TableDefinitionPartKind.Index, reader.GetString(0), reader.GetString(1)));
                if (!reader.IsDBNull(2) && reader.GetString(2).Length > 0)
                    parts.Add(new(TableDefinitionPartKind.Comment, reader.GetString(0),
                        $"COMMENT ON INDEX {Quote(schema)}.{Quote(reader.GetString(0))} IS {Literal(reader.GetString(2))}"));
            }

        if (!string.IsNullOrEmpty(comment))
            parts.Add(new(TableDefinitionPartKind.Comment, table.Name, $"COMMENT ON TABLE {qualified} IS {Literal(comment)}"));
        var userTypes = await UserTypesAsync(connection, catalog!, ct);
        var exclusions = new List<string> { "Table data." };
        await using (var cmd = Command(connection, """
            SELECT column_name, comment, column_default, data_type FROM duckdb_columns()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? ORDER BY column_index
            """, catalog, schema, table.Name))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var column = reader.GetString(0);
                if (!reader.IsDBNull(1) && reader.GetString(1).Length > 0)
                    parts.Add(new(TableDefinitionPartKind.Comment, column,
                        $"COMMENT ON COLUMN {qualified}.{Quote(column)} IS {Literal(reader.GetString(1))}"));
                if (!reader.IsDBNull(2))
                    foreach (Match m in NextValPattern().Matches(reader.GetString(2)))
                        prerequisites.Add($"Sequence {m.Groups[1].Value.Replace("''", "'")} (default of {Quote(column)})");
                if (!reader.IsDBNull(3) && userTypes.Contains(reader.GetString(3)))
                    exclusions.Add($"Column {Quote(column)} uses type {reader.GetString(3)}; the script declares its expanded definition.");
            }

        return new TableDefinitionSnapshot(table, "DuckDB", parts)
        {
            Prerequisites = prerequisites.Distinct().ToList(),
            Exclusions = exclusions
        };
    }

    /// <summary>Copies the stored table and index DDL under new names. Comments are not copied.</summary>
    public static TableDuplicatePlan PlanDuplicate(TableDefinitionSnapshot snapshot, DatabaseObjectIdentity table,
        string newName, bool includeData)
    {
        var schema = Quote(table.Schema ?? "main") + ".";
        var create = snapshot.Parts.First(p => p.Kind == TableDefinitionPartKind.Table).Sql;
        var statements = new List<string> { DefinitionNames.RenameTable(create, Quote(newName)) };
        statements.AddRange(snapshot.Parts.Where(p => p.Kind == TableDefinitionPartKind.Index).Select(p =>
            DefinitionNames.RenameIndex(p.Sql, Quote(DefinitionNames.Suffixed(p.Name, newName)), Quote(newName))));
        if (includeData)
        {
            var generated = DefinitionNames.GeneratedColumns(create);
            var columns = Builders.SqliteDefinitionSql.BodyTerms(create).Select(Builders.SqliteDefinitionSql.Tokens)
                .Where(t => t.Count > 1 && !t[0].Is("FOREIGN") && !t[0].Is("CHECK") && !t[0].Is("UNIQUE")
                            && !t[0].Is("PRIMARY") && !t[0].Is("CONSTRAINT"))
                .Select(t => t[0].Identifier).Where(c => !generated.Contains(c));
            statements.Add(DefinitionNames.CopyRows(Quote, schema + Quote(table.Name), schema + Quote(newName), columns));
        }
        var notCopied = new List<string>();
        if (snapshot.Parts.Any(p => p.Kind == TableDefinitionPartKind.Comment)) notCopied.Add("Comments.");
        if (create.Contains($"REFERENCES {schema}{Quote(table.Name)}(", StringComparison.Ordinal))
            notCopied.Add("Self-referencing foreign keys still reference the original table.");
        return new TableDuplicatePlan(table with { Name = newName }, statements, notCopied);
    }

    /// <summary>Replaces each FOREIGN KEY clause with one built from structured, quoted names.</summary>
    private static async Task<string> RepairForeignKeysAsync(DuckDBConnection connection, string catalog, string schema,
        string tableName, string createSql, List<TablePrerequisite> prerequisites, CancellationToken ct)
    {
        await using var cmd = Command(connection, """
            SELECT constraint_text, constraint_column_names, referenced_table, referenced_column_names
            FROM duckdb_constraints()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? AND constraint_type = 'FOREIGN KEY'
            ORDER BY constraint_index
            """, catalog, schema, tableName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var cursor = 0;
        while (await reader.ReadAsync(ct))
        {
            var text = reader.GetString(0);
            var columns = Names(reader.GetValue(1));
            var target = reader.GetString(2);
            var targetColumns = Names(reader.GetValue(3));
            var rebuilt = $"FOREIGN KEY ({string.Join(", ", columns.Select(Quote))}) REFERENCES " +
                          $"{Quote(schema)}.{Quote(target)}({string.Join(", ", targetColumns.Select(Quote))})";
            // DuckDB stores a self-referencing key as an empty term ("…, , …" or "…, )") with no text; the empty
            // terms come in constraint order, so each fills the next one.
            var at = text.Length > 0 ? createSql.IndexOf(text, StringComparison.Ordinal) : EmptyTerm(createSql, cursor);
            if (at < 0)
                throw new TableDefinitionUnavailableException(
                    $"Foreign key on {tableName} referencing {target} could not be located in the stored definition.");
            createSql = createSql[..at] + rebuilt + createSql[(at + text.Length)..];
            cursor = at + rebuilt.Length;
            if (!target.Equals(tableName, StringComparison.Ordinal))
                prerequisites.Add(new($"Referenced table {Quote(schema)}.{Quote(target)} (foreign key target)",
                    new DatabaseObjectIdentity(null, schema, target, SchemaNodeType.Table)));
        }
        return createSql;
    }

    /// <summary>Where the first empty body term at or after <paramref name="from"/> starts, after its comma and space; -1 if none.</summary>
    private static int EmptyTerm(string createSql, int from)
    {
        var match = EmptyTermPattern().Match(createSql, from);
        return match.Success ? match.Index + match.Length : -1;
    }

    [GeneratedRegex(@",\s*(?=[,)])")]
    private static partial Regex EmptyTermPattern();

    private static async Task<HashSet<string>> UserTypesAsync(DuckDBConnection connection, string catalog, CancellationToken ct)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Command(connection,
            "SELECT type_name FROM duckdb_types() WHERE NOT internal AND database_name = ?", catalog);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) types.Add(reader.GetString(0));
        return types;
    }

    private static List<string> Names(object value) => value switch
    {
        System.Collections.IEnumerable list and not string => list.Cast<object>().Select(v => v.ToString()!).ToList(),
        _ => throw new TableDefinitionUnavailableException("Foreign key column names are unavailable in this DuckDB version.")
    };

    private static DuckDBCommand Command(DuckDBConnection connection, string sql, params object?[] values)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var value in values) cmd.Parameters.Add(new DuckDBParameter { Value = value ?? DBNull.Value });
        return cmd;
    }

    [GeneratedRegex(@"nextval\('((?:[^']|'')+)'", RegexOptions.IgnoreCase)]
    private static partial Regex NextValPattern();
}
