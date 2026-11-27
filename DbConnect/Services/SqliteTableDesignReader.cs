using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Reads a SQLite <see cref="TableDesign"/>. Columns, keys, foreign keys, indexes and table
/// options come from the pragmas; constraint names, checks, column collations, generated
/// expressions and AUTOINCREMENT exist only in the stored CREATE TABLE text and are read from it
/// by <see cref="StoredTableText"/>. Where both hold a fact they are cross-checked, and a
/// disagreement fails the load instead of choosing one.
/// </summary>
internal sealed class SqliteTableDesignReader(SqliteConnection connection, string schema, CancellationToken ct)
{
    private const string Provider = "SQLite";
    private readonly List<TableDesignGap> _gaps = [];
    private string Pragma(string name, string argument) =>
        $"PRAGMA {Quote(schema)}.{name}('{argument.Replace("'", "''")}');";

    private static string Quote(string name) => ProviderCapabilityDeclarations.Sqlite.QuoteIdentifier(name);

    public static async Task<TableDesign> LoadAsync(string connectionString, DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        return await LoadAsync(connection, table, ct);
    }

    /// <summary>Reads through an open connection, which sees its attached and temp databases.</summary>
    internal static async Task<TableDesign> LoadAsync(SqliteConnection connection, DatabaseObjectIdentity table, CancellationToken ct)
    {
        try
        {
            return await new SqliteTableDesignReader(connection, table.Schema ?? "main", ct).ReadAsync(table);
        }
        catch (SqliteException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the design of {table.Name}: {ex.Message}", ex);
        }
    }

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static string Str(object? value) => Convert.ToString(value) ?? "";
    private static long Num(object? value) => Convert.ToInt64(value);

    private async Task<TableDesign> ReadAsync(DatabaseObjectIdentity requested)
    {
        var master = await RowsAsync($"SELECT type, name, sql FROM {Quote(schema)}.sqlite_master " +
                                     $"WHERE tbl_name = '{requested.Name.Replace("'", "''")}' COLLATE NOCASE AND sql IS NOT NULL ORDER BY rowid;");
        var tableRow = master.FirstOrDefault(r => Str(r[0]) == "table" && Str(r[1]).Equals(requested.Name, StringComparison.OrdinalIgnoreCase));
        if (tableRow == null)
            throw new TableDefinitionUnavailableException($"Table {requested.Name} has no stored definition in {schema}.");
        var name = Str(tableRow[1]);
        var createSql = Str(tableRow[2]);

        var info = (await RowsAsync(Pragma("table_list", name))).FirstOrDefault()
            ?? throw new TableDefinitionUnavailableException($"Table {name} was not found in {schema}.");
        var options = new List<TableDesignOption>();
        if (Num(info[4]) != 0) options.Add(new("WITHOUT ROWID", "true"));
        if (Num(info[5]) != 0) options.Add(new("STRICT", "true"));
        var isVirtual = Str(info[2]) == "virtual";
        StoredTableText text;
        if (isVirtual)
        {
            _gaps.Add(new("This is a virtual table; its module defines its structure.", name));
            text = new StoredTableText();
        }
        else text = StoredTableText.Parse(createSql);
        _gaps.AddRange(text.Unrecognised.Select(t => new TableDesignGap($"Unrecognised table clause: {t}")));
        var trusted = !isVirtual && text.Unrecognised.Count == 0;

        var (columns, primaryKeyColumns) = await ColumnsAsync(name, text, trusted, withoutRowId: Num(info[4]) != 0);
        var keyText = text.Constraints.Where(c => c.Kind == StoredConstraintKind.PrimaryKey).ToList();
        if (trusted && (keyText.Count > 1 || keyText.Count == 1 && !keyText[0].Columns.SequenceEqual(primaryKeyColumns, StringComparer.OrdinalIgnoreCase)))
            throw new TableDefinitionUnavailableException($"The primary key of {name} disagrees with its stored definition.");
        var (uniqueKeys, indexes) = await IndexesAsync(name, text, trusted, columns, master);
        return new TableDesign(requested with { Schema = schema, Name = name }, Provider)
        {
            Columns = columns,
            PrimaryKey = primaryKeyColumns.Count == 0 ? null : new TableDesignKey(keyText.FirstOrDefault()?.Name, primaryKeyColumns),
            UniqueKeys = uniqueKeys,
            ForeignKeys = await ForeignKeysAsync(name, text, trusted),
            ReferencedBy = await ReferencesAsync(name, primaryKeyColumns),
            Checks = text.Constraints.Where(c => c.Kind == StoredConstraintKind.Check)
                .Select(c => new TableDesignCheck(c.Name, c.Expression!) { Column = c.Column }).ToList(),
            Indexes = indexes,
            Triggers = Triggers(master),
            Options = options,
            Unsupported = new[]
            {
                TableDesignFeature.ColumnComments, TableDesignFeature.TableComment,
                TableDesignFeature.IndexIncludedColumns, TableDesignFeature.IndexMethod
            }.ToDictionary(f => f, _ => TableDesign.NotSupportedBy(Provider)),
            Unrepresented = _gaps,
            InDefaultSchema = schema.Equals("main", StringComparison.OrdinalIgnoreCase) || schema.Equals("temp", StringComparison.OrdinalIgnoreCase)
        };
    }

    private async Task<(List<TableDesignColumn>, List<string> PrimaryKey)> ColumnsAsync(string table, StoredTableText text, bool trusted,
        bool withoutRowId)
    {
        var rows = await RowsAsync(Pragma("table_xinfo", table));
        if (trusted && !rows.Where(r => Num(r[6]) != 1).Select(r => Str(r[1])).SequenceEqual(text.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
            throw new TableDefinitionUnavailableException($"The columns of {table} disagree with its stored definition.");
        var primaryKey = rows.Where(r => Num(r[5]) > 0).OrderBy(r => Num(r[5])).Select(r => Str(r[1])).ToList();
        var columns = new List<TableDesignColumn>();
        foreach (var row in rows)
        {
            var name = Str(row[1]);
            var stored = text.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var hidden = Num(row[6]);
            if (hidden == 1) continue;
            var generated = hidden is 2 or 3;
            if (trusted && (generated != (stored?.GeneratedExpression != null) || generated && stored!.GeneratedStored != (hidden == 3)))
                throw Disagreement(table, $"the generation of column {name}");
            var type = Str(row[2]);
            var rowIdKey = !withoutRowId && primaryKey.Count == 1 && primaryKey[0] == name && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
            columns.Add(new TableDesignColumn(name, type, Num(row[3]) == 0)
            {
                Default = row[4] == null ? null : Str(row[4]),
                Collation = stored?.Collation,
                Generated = hidden is 2 or 3 && stored?.GeneratedExpression is { } expression
                    ? new TableDesignGenerated(expression, hidden == 3 ? TableDesignGeneratedStorage.Stored : TableDesignGeneratedStorage.Virtual)
                    : null,
                Identity = stored?.Autoincrement == true ? new TableDesignIdentity(TableDesignIdentityKind.AutoIncrement)
                    : rowIdKey ? new TableDesignIdentity(TableDesignIdentityKind.RowIdAlias) : null
            });
        }
        return (columns, primaryKey);
    }

    /// <summary>
    /// Foreign keys of every table in the schema (this one included) that reference <paramref name="table"/>.
    /// SQLite keeps their names only in the referencing table's stored text, so they are matched there.
    /// </summary>
    private async Task<List<TableDesignReference>> ReferencesAsync(string table, List<string> primaryKey)
    {
        var references = new List<TableDesignReference>();
        var tables = await RowsAsync($"SELECT name, sql FROM {Quote(schema)}.sqlite_master WHERE type = 'table' AND sql IS NOT NULL ORDER BY name;");
        foreach (var row in tables)
        {
            var owner = Str(row[0]);
            var rows = (await RowsAsync(Pragma("foreign_key_list", owner)))
                .Where(r => Str(r[2]).Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var declared = StoredTableText.Parse(Str(row[1])).Constraints.Where(c => c.Kind == StoredConstraintKind.ForeignKey).ToList();
            foreach (var group in rows.GroupBy(r => Num(r[0])).OrderByDescending(g => g.Key))
            {
                var ordered = group.OrderBy(r => Num(r[1])).ToList();
                var from = ordered.Select(r => Str(r[3])).ToList();
                // A foreign key without listed columns references the primary key.
                var to = ordered.Any(r => r[4] == null) ? primaryKey : ordered.Select(r => Str(r[4])).ToList();
                var name = declared.FirstOrDefault(c => c.ReferencedTable!.Equals(table, StringComparison.OrdinalIgnoreCase)
                                                        && c.Columns.SequenceEqual(from, StringComparer.OrdinalIgnoreCase))?.Name;
                references.Add(new TableDesignReference(name, new DatabaseObjectIdentity(null, schema, owner, SchemaNodeType.Table), to));
            }
        }
        return references;
    }

    private async Task<List<TableDesignForeignKey>> ForeignKeysAsync(string table, StoredTableText text, bool trusted)
    {
        var rows = await RowsAsync(Pragma("foreign_key_list", table));
        var declared = text.Constraints.Where(c => c.Kind == StoredConstraintKind.ForeignKey).ToList();
        var keys = new List<TableDesignForeignKey>();
        foreach (var group in rows.GroupBy(r => Num(r[0])).OrderByDescending(g => g.Key))
        {
            var ordered = group.OrderBy(r => Num(r[1])).ToList();
            var target = Str(ordered[0][2]);
            var from = ordered.Select(r => Str(r[3])).ToList();
            var to = ordered.Select(r => r[4] == null ? null : Str(r[4])).ToList();
            if (to.Any(t => t == null))
            {
                var parentKey = (await RowsAsync(Pragma("table_info", target))).Where(r => Num(r[5]) > 0).OrderBy(r => Num(r[5]))
                    .Select(r => Str(r[1])).ToList();
                if (parentKey.Count != from.Count)
                    throw Disagreement(table, $"the columns a foreign key references in {target}");
                to = parentKey!;
            }
            var match = declared.FirstOrDefault(c => c.ReferencedTable!.Equals(target, StringComparison.OrdinalIgnoreCase)
                                                     && c.Columns.SequenceEqual(from, StringComparer.OrdinalIgnoreCase));
            if (trusted && match == null) throw Disagreement(table, $"a foreign key to {target}");
            if (match != null) declared.Remove(match);
            var matchOption = Str(ordered[0][7]);
            var state = string.Join(" ", new[] { matchOption is "NONE" or "" ? null : $"MATCH {matchOption}", match?.Deferrable }.OfType<string>());
            keys.Add(new TableDesignForeignKey(match?.Name, from.Zip(to, (f, t) => new TableDesignColumnPair(f, t!)).ToList(),
                new DatabaseObjectIdentity(null, schema, target, SchemaNodeType.Table), Str(ordered[0][6]), Str(ordered[0][5]))
            {
                State = state.Length == 0 ? null : state
            });
        }
        if (trusted && declared.Count > 0) throw Disagreement(table, "the foreign keys");
        return keys;
    }

    private async Task<(List<TableDesignKey>, List<TableDesignIndex>)> IndexesAsync(string table, StoredTableText text, bool trusted,
        List<TableDesignColumn> columns, List<object?[]> master)
    {
        var uniqueText = text.Constraints.Where(c => c.Kind == StoredConstraintKind.Unique).ToList();
        var uniqueKeys = new List<TableDesignKey>();
        var indexes = new List<TableDesignIndex>();
        // Autoindexes are numbered in declaration order; index_list lists the newest first.
        foreach (var index in (await RowsAsync(Pragma("index_list", table))).OrderBy(r => Str(r[1]), StringComparer.Ordinal))
        {
            var name = Str(index[1]);
            var origin = Str(index[3]);
            var sql = master.FirstOrDefault(r => Str(r[0]) == "index" && Str(r[1]) == name)?[2] as string;
            var terms = sql == null ? null : SqliteDefinitionSql.BodyTerms(sql);
            var parts = new List<TableDesignIndexPart>();
            foreach (var row in (await RowsAsync(Pragma("index_xinfo", name))).Where(r => Num(r[5]) == 1).OrderBy(r => Num(r[0])))
            {
                var cid = Num(row[1]);
                var column = cid >= 0 ? Str(row[2]) : cid == -1 ? "rowid" : null;
                var expression = column == null ? KeyExpression(terms, (int)Num(row[0]), table) : null;
                var expected = column == null ? "BINARY"
                    : columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))?.Collation ?? "BINARY";
                var collation = Str(row[4]);
                parts.Add(new TableDesignIndexPart(column, expression, Num(row[3]) != 0)
                {
                    Collation = collation.Equals(expected, StringComparison.OrdinalIgnoreCase) ? null : collation
                });
            }
            if (origin == "u")
            {
                var keyColumns = parts.Select(p => p.Column).ToList();
                var declared = uniqueText.FirstOrDefault(c => c.Columns.SequenceEqual(keyColumns, StringComparer.OrdinalIgnoreCase));
                if (trusted && declared == null) throw Disagreement(table, $"unique key {name}");
                if (declared != null) uniqueText.Remove(declared);
                uniqueKeys.Add(new TableDesignKey(declared?.Name, keyColumns!));
            }
            indexes.Add(new TableDesignIndex(name, Num(index[2]) != 0, parts, origin switch
            {
                "pk" => TableDesignIndexOrigin.PrimaryKey,
                "u" => TableDesignIndexOrigin.UniqueKey,
                _ => TableDesignIndexOrigin.CreateIndex
            })
            {
                Filter = Num(index[4]) != 0 && sql != null ? SqliteDefinitionSql.IndexPredicate(sql) : null
            });
        }
        return (uniqueKeys, indexes.OrderBy(i => i.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>The text of an expression key part, without its COLLATE and ASC/DESC suffix.</summary>
    private static string KeyExpression(List<string>? terms, int position, string table)
    {
        if (terms == null || position >= terms.Count) throw Disagreement(table, "an index expression");
        var term = terms[position];
        var cut = SqliteDefinitionSql.Tokens(term).FirstOrDefault(t => t.Depth == 0 && (t.Is("COLLATE") || t.Is("ASC") || t.Is("DESC")));
        return (cut == null ? term : term[..cut.Start]).Trim();
    }

    private static List<TableDesignTrigger> Triggers(List<object?[]> master) => master
        .Where(r => Str(r[0]) == "trigger")
        .Select(r =>
        {
            var sql = Str(r[2]);
            var tokens = SqliteDefinitionSql.Tokens(sql);
            var on = tokens.FindIndex(t => t.Is("ON"));
            var at = tokens.FindIndex(t => t.Is("BEFORE") || t.Is("AFTER") || t.Is("INSTEAD"));
            var timing = at < 0 || at > on ? "BEFORE" : tokens[at].Is("INSTEAD") ? "INSTEAD OF" : tokens[at].Text.ToUpperInvariant();
            var eventStart = tokens.FindIndex(t => t.Is("DELETE") || t.Is("INSERT") || t.Is("UPDATE"));
            var trigger = eventStart < 0 || on < eventStart ? "" : sql[tokens[eventStart].Start..tokens[on - 1].End];
            return new TableDesignTrigger(Str(r[1]), timing, $"{trigger} FOR EACH ROW", sql);
        })
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();

    private static TableDefinitionUnavailableException Disagreement(string table, string what) =>
        new($"The catalog and the stored definition of {table} disagree about {what}.");
}
