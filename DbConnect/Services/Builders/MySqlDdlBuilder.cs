using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed partial class MySqlDdlBuilder : IDdlBuilder
{
    private readonly string? _owner;

    public MySqlDdlBuilder(string? owner = null) => _owner = owner;

    public string ProviderName => "MySQL";

    public ColumnTypeRules TypeRules => ColumnTypeRules.MySql;

    public ColumnGenerationRules GenerationRules => ColumnGenerationRules.MySql;

    public TableKeyRules KeyRules => TableKeyRules.MySql;

    public TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.MySql;

    public TableCheckRules CheckRules => TableCheckRules.MySql;

    /// <summary>One ALTER TABLE per change; MySQL accepts a same-named DROP CHECK and ADD in one statement.</summary>
    public IReadOnlyList<string> GenerateCheckChange(string tableName, TableCheckChange change)
    {
        var clauses = new List<string>();
        if (change.From is { } from) clauses.Add($"DROP CHECK {Quote(from.Address)}");
        if (change.To is { } to) clauses.Add($"ADD {DdlColumnRules.CheckClause(to, Quote)}");
        return [$"ALTER TABLE {Table(tableName)} {string.Join(", ", clauses)};"];
    }

    /// <summary>One ALTER TABLE per change; the planner sends a replacement as its drop and its add (MySQL error 1826).</summary>
    public IReadOnlyList<string> GenerateForeignKeyChange(string tableName, TableForeignKeyChange change)
    {
        var clauses = new List<string>();
        if (change.From is { } from) clauses.Add($"DROP FOREIGN KEY {Quote(from.Address)}");
        if (change.To is { } to) clauses.Add($"ADD {DdlColumnRules.ForeignKeyClause(to, Quote, Referenced)}");
        return [$"ALTER TABLE {Table(tableName)} {string.Join(", ", clauses)};"];
    }

    /// <summary>One ALTER TABLE per change, so replacing a key either completes or leaves the old one.</summary>
    public IReadOnlyList<string> GenerateKeyChange(string tableName, TableKeyChange change)
    {
        if (DdlColumnRules.IsRename(change))
            return [$"ALTER TABLE {Table(tableName)} RENAME INDEX {Quote(change.From!.Address)} TO {Quote(change.To!.Name!)};"];
        var clauses = new List<string>();
        if (change.From is { } from) clauses.Add(from.Kind == KeyKind.Primary ? "DROP PRIMARY KEY" : $"DROP INDEX {Quote(from.Address)}");
        if (change.To is { } to)
            clauses.Add(to.Kind == KeyKind.Primary
                ? $"ADD PRIMARY KEY ({string.Join(", ", to.Columns.Select(Quote))})"
                : $"ADD UNIQUE {(to.Name != null ? $"KEY {Quote(to.Name)} " : "")}({string.Join(", ", to.Columns.Select(Quote))})");
        return [$"ALTER TABLE {Table(tableName)} {string.Join(", ", clauses)};"];
    }

    public TableIndexRules IndexRules => TableIndexRules.MySql;

    /// <summary>One ALTER TABLE per change, so re-creating an index either completes or leaves the old one.</summary>
    public IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change)
    {
        if (DdlColumnRules.IsRename(change))
            return [$"ALTER TABLE {Table(tableName)} RENAME INDEX {Quote(change.From!.Name)} TO {Quote(change.To!.Name)};"];
        var clauses = new List<string>();
        if (change.From is { } from) clauses.Add($"DROP INDEX {Quote(from.Name)}");
        if (change.To is { } to) clauses.Add($"ADD {DdlIndexSql.Unique(to)}INDEX {Quote(to.Name)} ({DdlIndexSql.Parts(to, Quote)})");
        return [$"ALTER TABLE {Table(tableName)} {string.Join(", ", clauses)};"];
    }

    public IDdlBuilder InSchema(string? owner) => new MySqlDdlBuilder(owner);

    public string Quote(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    // The owner is the database; indexes belong to their table.
    private string Table(string name) => _owner == null ? Quote(name) : $"{Quote(_owner)}.{Quote(name)}";

    public bool ColumnCommentsInDefinition => true;

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns) =>
        GenerateCreateTable(tableName, columns, []);

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, IList<ForeignKeyDefinition> foreignKeys) =>
        GenerateCreateTable(tableName, columns, DdlColumnRules.ConstraintsFrom(columns, foreignKeys));

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, TableConstraints constraints)
    {
        var (keys, foreignKeys, checks) = (constraints.Keys, constraints.ForeignKeys, constraints.Checks);
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Table(tableName)} (");

        var lines = columns.Select(c => $"    {BuildColumnString(c)}").ToList();
        lines.AddRange(keys.Select(k => "    " + DdlColumnRules.KeyClause(k, Quote)));
        lines.AddRange(foreignKeys.Select(fk => "    " + DdlColumnRules.ForeignKeyClause(fk, Quote, Referenced)));
        lines.AddRange(checks.Select(c => "    " + DdlColumnRules.CheckClause(c, Quote)));
        sb.AppendLine(string.Join("," + Environment.NewLine, lines));
        sb.Append(");");
        return sb.ToString();
    }

    public string GenerateAlterTable(string tableName, IList<ColumnDefinition> original, IList<ColumnDefinition> modified, IList<IndexDefinition>? indexes = null, TableRebuildDefinition? definition = null)
    {
        var sb = new StringBuilder();
        var table = Table(tableName);

        foreach (var col in modified.Where(c => c.IsDropped && !c.IsNew))
            sb.AppendLine($"ALTER TABLE {table} DROP COLUMN {Quote((DdlColumnRules.OriginalOf(col, original) ?? col).Name)};");

        foreach (var col in modified.Where(c => !c.IsNew && !c.IsDropped))
        {
            var orig = DdlColumnRules.OriginalOf(col, original);
            if (orig == null || !IsChanged(orig, col)) continue;
            // MySQL CHANGE renames; MODIFY changes type/nullability
            // An unchanged type is restated exactly as loaded (COLUMN_TYPE keeps length, precision and UNSIGNED).
            var type = orig.DataType == col.DataType && col.Loaded != null ? col.Loaded.DataType : col.DataType;
            // An unchanged default is restated as loaded: the designer's field holds the catalog's unquoted value.
            var restated = col;
            if (orig.DefaultValue == col.DefaultValue && col.Loaded != null)
            {
                restated = col.Clone();
                restated.DefaultValue = col.Loaded.Default;
            }
            if (orig.Name != col.Name)
                sb.AppendLine($"ALTER TABLE {table} CHANGE {Quote(orig.Name)} {BuildColumnString(restated, type)};");
            else
                sb.AppendLine($"ALTER TABLE {table} MODIFY {BuildColumnString(restated, type)};");
        }

        // New columns go last: a dropped or renamed name is free by then, and a new generated column reads altered types.
        foreach (var col in modified.Where(c => c.IsNew && !c.IsDropped))
            sb.AppendLine($"ALTER TABLE {table} ADD {BuildColumnString(col)};");

        return sb.ToString().TrimEnd();
    }

    public string GenerateCreateIndex(IndexDefinition index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var cols = string.Join(", ", index.Columns.Select((c, i) =>
        {
            var dir = i < index.ColumnDirections.Count ? index.ColumnDirections[i] : IndexColumnDirection.Asc;
            return $"{Quote(c)} {(dir == IndexColumnDirection.Desc ? "DESC" : "ASC")}";
        }));
        return $"CREATE {unique}INDEX {Quote(index.Name)} ON {Table(index.TableName)} ({cols});";
    }

    public string GenerateDropIndex(string indexName, string? tableName = null)
    {
        if (!string.IsNullOrEmpty(tableName))
            return $"DROP INDEX {Quote(indexName)} ON {Table(tableName)};";
        return $"DROP INDEX {Quote(indexName)};";
    }

    public string GenerateCreateView(string viewName, string selectSql, bool createOrReplace = false)
    {
        var keyword = createOrReplace ? "CREATE OR REPLACE VIEW" : "CREATE VIEW";
        return $"{keyword} {Quote(viewName)} AS{Environment.NewLine}{selectSql};";
    }

    public string GenerateAddUniqueConstraint(string tableName, string constraintName, IList<string> columns)
    {
        var cols = string.Join(", ", columns.Select(Quote));
        return $"ALTER TABLE {Table(tableName)} ADD CONSTRAINT {Quote(constraintName)} UNIQUE ({cols});";
    }

    public string GenerateAddForeignKey(ForeignKeyDefinition fk) =>
        $"ALTER TABLE {Table(fk.Table)} ADD {DdlColumnRules.ForeignKeyClause(fk, Quote)};";

    public string GenerateDropConstraint(string tableName, string constraintName) =>
        $"ALTER TABLE {Table(tableName)} DROP CONSTRAINT {Quote(constraintName)};";

    /// <summary>The table comment is a table option; column comments are carried by the column definitions.</summary>
    public IReadOnlyList<string> GenerateCommentChanges(string tableName, IReadOnlyList<CommentChange> changes) =>
        changes.Where(c => c.Column == null)
            .Select(c => $"ALTER TABLE {Table(tableName)} COMMENT = {Literal(c.Comment ?? "")};").ToList();

    /// <summary>
    /// MODIFY and CHANGE restate the whole column from the designer's fields: its type, collation,
    /// generation, AUTO_INCREMENT, nullability and comment, and its loaded default when that is a
    /// plain literal or CURRENT_TIMESTAMP. An expression default or other engine property would be dropped.
    /// </summary>
    public string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded)
    {
        var kept = new List<string>();
        if (loaded.Default != null && original.DefaultValue == modified.DefaultValue && !RestatableDefault().IsMatch(loaded.Default))
            kept.Add("default");
        kept.AddRange(loaded.Properties);
        return kept.Count == 0 ? null
            : $"changing it restates the whole column, which would drop its {string.Join(", ", kept)}; editing them is not available.";
    }

    /// <summary>
    /// Defaults the reader gives as SQL that MODIFY can restate unchanged: a quoted literal without
    /// backslashes (MySQL would read them as escapes), a number, NULL or CURRENT_TIMESTAMP.
    /// </summary>
    [GeneratedRegex(@"^('([^'\\]|'')*'|-?\d+(\.\d+)?|NULL|CURRENT_TIMESTAMP(\(\d\))?)$", RegexOptions.IgnoreCase)]
    private static partial Regex RestatableDefault();

    // MySQL treats backslash as an escape in string literals by default, so it is doubled along with quotes.
    private static string Literal(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>A referenced table, qualified as far as its identity says.</summary>
    private string Referenced(DatabaseObjectIdentity identity) => DdlColumnRules.QualifiedTable(identity, Quote, byDatabase: true);

    private string BuildColumnString(ColumnDefinition col, string? type = null)
    {
        var sb = new StringBuilder();
        sb.Append($"{Quote(col.Name)} {type ?? col.DataType}{DdlColumnRules.Collate(col)}{DdlColumnRules.GeneratedClause(col)}");
        if (col.IsIdentity) sb.Append(" AUTO_INCREMENT");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (!string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        if (!string.IsNullOrEmpty(col.Comment)) sb.Append($" COMMENT {Literal(col.Comment)}");
        return sb.ToString();
    }

    private static bool IsChanged(ColumnDefinition a, ColumnDefinition b) =>
        a.Name != b.Name || a.DataType != b.DataType || a.Collation != b.Collation || a.IsNullable != b.IsNullable
        || a.DefaultValue != b.DefaultValue || DdlColumnRules.GenerationChanged(a, b)
        || (a.Comment ?? "") != (b.Comment ?? "");
}
