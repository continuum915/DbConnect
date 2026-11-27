using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Reads a MySQL <see cref="TableDesign"/> from INFORMATION_SCHEMA: COLUMNS (EXTRA carries
/// ON UPDATE and generation), TABLE_CONSTRAINTS, KEY_COLUMN_USAGE, REFERENTIAL_CONSTRAINTS,
/// CHECK_CONSTRAINTS, STATISTICS, TRIGGERS and TABLES.
/// </summary>
internal sealed class MySqlTableDesignReader(MySqlConnection connection, string database, string table, CancellationToken ct)
{
    private const string Provider = "MySQL";
    private readonly List<TableDesignGap> _gaps = [];

    public static async Task<TableDesign> LoadAsync(string connectionString, DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        var database = table.Catalog ?? table.Schema ?? connection.Database;
        try
        {
            return await new MySqlTableDesignReader(connection, database, table.Name, ct).ReadAsync(table);
        }
        catch (MySqlException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the design of {table.Name}: {ex.Message}", ex);
        }
    }

    private MySqlCommand Command(string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table);
        return cmd;
    }

    /// <summary>
    /// GENERATION_EXPRESSION and CHECK_CLAUSE escape quotes and backslashes with a backslash (concat(`a`,_utf8mb4\' \')); this
    /// gives back the expression as SQL, so it can be shown, edited and restated by MODIFY.
    /// </summary>
    private static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
            sb.Append(text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '\'' ? text[++i] : text[i]);
        return sb.ToString();
    }

    private static string? Text(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private async Task<TableDesign> ReadAsync(DatabaseObjectIdentity requested)
    {
        var (options, comment, collation) = await TableAsync();
        var columns = await ColumnsAsync(collation);
        var (primaryKey, uniqueKeys, indexes) = await IndexesAsync();
        return new TableDesign(requested with { Catalog = database, Schema = null, Name = table }, Provider)
        {
            Columns = columns,
            PrimaryKey = primaryKey,
            UniqueKeys = uniqueKeys,
            ForeignKeys = await ForeignKeysAsync(),
            ReferencedBy = await ReferencesAsync(),
            Checks = await ChecksAsync(),
            Indexes = indexes,
            Triggers = await TriggersAsync(),
            Comment = comment,
            Options = options,
            Unsupported = new Dictionary<TableDesignFeature, string>
            {
                [TableDesignFeature.IndexIncludedColumns] = TableDesign.NotSupportedBy(Provider),
                [TableDesignFeature.IndexFilter] = TableDesign.NotSupportedBy(Provider)
            },
            Unrepresented = _gaps,
            InDefaultSchema = database == connection.Database
        };
    }

    private async Task<(List<TableDesignOption> Options, string? Comment, string? Collation)> TableAsync()
    {
        await using var cmd = Command("""
            SELECT TABLE_TYPE, ENGINE, TABLE_COLLATION, CREATE_OPTIONS, TABLE_COMMENT
            FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != "BASE TABLE")
            throw new TableDefinitionUnavailableException($"Table {database}.{table} was not found.");
        var options = new List<TableDesignOption>();
        if (Text(reader, 1) is { } engine) options.Add(new("ENGINE", engine));
        var collation = Text(reader, 2);
        if (collation != null) options.Add(new("COLLATE", collation));
        foreach (var option in (Text(reader, 3) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (option.Equals("partitioned", StringComparison.OrdinalIgnoreCase))
            {
                _gaps.Add(new("This table is partitioned.", table));
                continue;
            }
            var at = option.IndexOf('=');
            options.Add(at < 0 ? new(option, "") : new(option[..at].ToUpperInvariant(), option[(at + 1)..]));
        }
        var comment = Text(reader, 4);
        return (options, string.IsNullOrEmpty(comment) ? null : comment, collation);
    }

    private static readonly HashSet<string> Unquoted = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "bigint", "decimal", "float", "double", "bit", "year"
    };

    private async Task<List<TableDesignColumn>> ColumnsAsync(string? tableCollation)
    {
        var columns = new List<TableDesignColumn>();
        await using var cmd = Command("""
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLLATION_NAME, GENERATION_EXPRESSION,
                   COLUMN_COMMENT, DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var extra = Text(reader, 4) ?? "";
            var properties = new List<string>();
            var onUpdate = extra.IndexOf("on update ", StringComparison.OrdinalIgnoreCase);
            if (onUpdate >= 0) properties.Add("ON UPDATE " + extra[(onUpdate + 10)..].Split(' ')[0]);
            if (extra.Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase)) properties.Add("INVISIBLE");
            var expressionDefault = extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase);
            var generated = extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase) ? TableDesignGeneratedStorage.Stored
                : extra.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) ? TableDesignGeneratedStorage.Virtual
                : (TableDesignGeneratedStorage?)null;
            var value = Text(reader, 3);
            var collation = Text(reader, 5);
            var comment = Text(reader, 7);
            columns.Add(new TableDesignColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES")
            {
                Default = value == null || generated != null ? null
                    : expressionDefault || Unquoted.Contains(reader.GetString(8)) ? value
                    : "'" + value.Replace("'", "''") + "'",
                Collation = collation != null && collation != tableCollation ? collation : null,
                Generated = generated is { } storage ? new TableDesignGenerated(Unescape(Text(reader, 6) ?? ""), storage) : null,
                Identity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase)
                    ? new TableDesignIdentity(TableDesignIdentityKind.AutoIncrement) : null,
                Comment = string.IsNullOrEmpty(comment) ? null : comment,
                Properties = properties
            });
        }
        return columns;
    }

    private async Task<(TableDesignKey? PrimaryKey, List<TableDesignKey> UniqueKeys, List<TableDesignIndex> Indexes)> IndexesAsync()
    {
        var rows = new List<(string Index, bool Unique, string? Column, string? Expression, string? Order, long? Prefix,
            string Type, string? Comment, bool Visible)>();
        await using (var cmd = Command("""
            SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME, EXPRESSION, COLLATION, SUB_PART, INDEX_TYPE, INDEX_COMMENT, IS_VISIBLE
            FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t
            ORDER BY INDEX_NAME = 'PRIMARY' DESC, INDEX_NAME, SEQ_IN_INDEX
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), Convert.ToInt64(reader.GetValue(1)) == 0, Text(reader, 2), Text(reader, 3),
                    Text(reader, 4), reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5)), reader.GetString(6),
                    Text(reader, 7), reader.GetString(8) == "YES"));

        TableDesignKey? primaryKey = null;
        var uniqueKeys = new List<TableDesignKey>();
        var indexes = new List<TableDesignIndex>();
        foreach (var group in rows.GroupBy(r => r.Index))
        {
            var first = group.First();
            var parts = group.Select(r => new TableDesignIndexPart(r.Column, r.Column == null ? r.Expression : null, r.Order == "D")
            {
                Options = r.Prefix is { } prefix ? $"PREFIX {prefix}" : null
            }).ToList();
            var primary = group.Key == "PRIMARY";
            var keyed = first.Unique && parts.All(p => p.Column != null && p.Options == null);
            if (primary) primaryKey = new TableDesignKey(null, parts.Select(p => p.Column!).ToList());
            else if (keyed) uniqueKeys.Add(new TableDesignKey(group.Key, parts.Select(p => p.Column!).ToList()));
            indexes.Add(new TableDesignIndex(group.Key, first.Unique, parts,
                primary ? TableDesignIndexOrigin.PrimaryKey : keyed ? TableDesignIndexOrigin.UniqueKey : TableDesignIndexOrigin.CreateIndex)
            {
                Method = first.Type,
                Properties = first.Visible ? [] : ["INVISIBLE"],
                Comment = string.IsNullOrEmpty(first.Comment) ? null : first.Comment
            });
        }
        return (primaryKey, uniqueKeys, indexes);
    }

    private async Task<List<TableDesignForeignKey>> ForeignKeysAsync()
    {
        var keys = new List<TableDesignForeignKey>();
        await using var cmd = Command("""
            SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME,
                   r.DELETE_RULE, r.UPDATE_RULE, r.MATCH_OPTION
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS r
              ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME
            WHERE k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @t AND k.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """);
        var rows = new List<(string Name, string Column, string Referenced, string Schema, string Table, string Delete, string Update, string Match)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        foreach (var group in rows.GroupBy(r => r.Name))
        {
            var first = group.First();
            keys.Add(new TableDesignForeignKey(group.Key, group.Select(r => new TableDesignColumnPair(r.Column, r.Referenced)).ToList(),
                new DatabaseObjectIdentity(first.Schema, null, first.Table, SchemaNodeType.Table), first.Delete, first.Update)
            {
                State = first.Match is "NONE" or "SIMPLE" ? null : $"MATCH {first.Match}"
            });
        }
        return keys;
    }

    private async Task<List<TableDesignReference>> ReferencesAsync()
    {
        await using var cmd = Command("""
            SELECT k.CONSTRAINT_NAME, k.TABLE_SCHEMA, k.TABLE_NAME, k.REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            WHERE k.REFERENCED_TABLE_SCHEMA = @db AND BINARY k.REFERENCED_TABLE_NAME = BINARY @t
            ORDER BY k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """);
        var rows = new List<(string Name, string Schema, string Table, string Column)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows.GroupBy(r => (r.Schema, r.Table, r.Name)).Select(g => new TableDesignReference(g.Key.Name,
            new DatabaseObjectIdentity(g.Key.Schema, null, g.Key.Table, SchemaNodeType.Table), g.Select(r => r.Column).ToList())).ToList();
    }

    private async Task<List<TableDesignCheck>> ChecksAsync()
    {
        var checks = new List<TableDesignCheck>();
        await using var cmd = Command("""
            SELECT c.CONSTRAINT_NAME, c.CHECK_CLAUSE, t.ENFORCED
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t
            JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS c
              ON c.CONSTRAINT_SCHEMA = t.CONSTRAINT_SCHEMA AND c.CONSTRAINT_NAME = t.CONSTRAINT_NAME
            WHERE t.TABLE_SCHEMA = @db AND t.TABLE_NAME = @t AND t.CONSTRAINT_TYPE = 'CHECK'
            ORDER BY c.CONSTRAINT_NAME
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            // CHECK_CLAUSE escapes quotes like GENERATION_EXPRESSION, so it is unescaped back into SQL to be shown and re-added.
            checks.Add(new TableDesignCheck(reader.GetString(0), Unescape(reader.GetString(1)))
            {
                State = reader.GetString(2) == "NO" ? "NOT ENFORCED" : null
            });
        return checks;
    }

    private async Task<List<TableDesignTrigger>> TriggersAsync()
    {
        var triggers = new List<TableDesignTrigger>();
        await using var cmd = Command("""
            SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_STATEMENT FROM INFORMATION_SCHEMA.TRIGGERS
            WHERE EVENT_OBJECT_SCHEMA = @db AND EVENT_OBJECT_TABLE = @t
            ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            triggers.Add(new TableDesignTrigger(reader.GetString(0), reader.GetString(1), $"{reader.GetString(2)} FOR EACH ROW",
                $"CREATE TRIGGER {ProviderCapabilityDeclarations.MySql.QuoteIdentifier(reader.GetString(0))} {reader.GetString(1)} " +
                $"{reader.GetString(2)} ON {ProviderCapabilityDeclarations.MySql.QuoteIdentifier(table)} FOR EACH ROW {reader.GetString(3)}"));
        return triggers;
    }
}
