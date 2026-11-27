using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed class PostgreSqlDdlBuilder : IDdlBuilder
{
    private readonly string? _owner;

    public PostgreSqlDdlBuilder(string? owner = null) => _owner = owner;

    public string ProviderName => "PostgreSQL";

    public ColumnTypeRules TypeRules => ColumnTypeRules.PostgreSql;

    public ColumnGenerationRules GenerationRules => ColumnGenerationRules.PostgreSql;

    public TableKeyRules KeyRules => TableKeyRules.PostgreSql;

    public TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.PostgreSql;

    public IReadOnlyList<string> GenerateForeignKeyChange(string tableName, TableForeignKeyChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.ForeignKeyClause(to, Quote, Referenced) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    public IReadOnlyList<string> GenerateKeyChange(string tableName, TableKeyChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.KeyClause(to, Quote) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    public TableCheckRules CheckRules => TableCheckRules.PostgreSql;

    public IReadOnlyList<string> GenerateCheckChange(string tableName, TableCheckChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.CheckClause(to, Quote) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    /// <summary>Renames the constraint <paramref name="drop"/> names, or drops it and adds <paramref name="add"/>.</summary>
    private IReadOnlyList<string> ConstraintChange(string tableName, string? drop, string? add, string? renameTo)
    {
        var table = Table(tableName);
        if (renameTo != null) return [$"ALTER TABLE {table} RENAME CONSTRAINT {Quote(drop!)} TO {Quote(renameTo)};"];
        var statements = new List<string>();
        if (drop != null) statements.Add($"ALTER TABLE {table} DROP CONSTRAINT {Quote(drop)};");
        if (add != null) statements.Add($"ALTER TABLE {table} ADD {add};");
        return statements;
    }

    public TableIndexRules IndexRules => TableIndexRules.PostgreSql;

    public IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change)
    {
        if (DdlColumnRules.IsRename(change)) return [$"ALTER INDEX {Table(change.From!.Name)} RENAME TO {Quote(change.To!.Name)};"];
        var statements = new List<string>();
        if (change.From is { } from) statements.Add($"DROP INDEX {Table(from.Name)};");
        if (change.To is { } to)
        {
            var method = to.Method != null && !to.Method.Equals("btree", StringComparison.OrdinalIgnoreCase) ? $" USING {to.Method}" : "";
            statements.Add(DdlIndexSql.Create(to, Table(tableName), Quote, method: method));
        }
        return statements;
    }

    public IDdlBuilder InSchema(string? owner) => new PostgreSqlDdlBuilder(owner);

    public string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    // Tables and indexes both live in the table's schema.
    private string Table(string name) => _owner == null ? Quote(name) : $"{Quote(_owner)}.{Quote(name)}";

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
        var expressions = new StringBuilder();
        var table = Table(tableName);

        foreach (var col in modified.Where(c => c.IsDropped && !c.IsNew))
            sb.AppendLine($"ALTER TABLE {table} DROP COLUMN {Quote((DdlColumnRules.OriginalOf(col, original) ?? col).Name)};");

        foreach (var col in modified.Where(c => !c.IsNew && !c.IsDropped))
        {
            var orig = DdlColumnRules.OriginalOf(col, original);
            if (orig == null || !IsChanged(orig, col)) continue;
            var column = $"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)}";
            if (orig.Name != col.Name)
                sb.AppendLine($"ALTER TABLE {table} RENAME COLUMN {Quote(orig.Name)} TO {Quote(col.Name)};");
            var generation = ColumnGeneration.Changes(orig, col);
            // Removing a generator comes first, so a default or nullability change can follow it.
            if (generation.HasFlag(ColumnGenerationChange.DropIdentity)) sb.AppendLine($"{column} DROP IDENTITY;");
            if (generation.HasFlag(ColumnGenerationChange.DropGeneration)) sb.AppendLine($"{column} DROP EXPRESSION;");
            // A collation is set with the type; TYPE without COLLATE returns the column to the type's default.
            if (DdlColumnRules.TypeOrCollationChanged(orig, col))
                sb.AppendLine($"{column} TYPE {col.DataType}{DdlColumnRules.Collate(col)};");
            if (orig.IsNullable != col.IsNullable)
                sb.AppendLine(col.IsNullable ? $"{column} DROP NOT NULL;" : $"{column} SET NOT NULL;");
            if (orig.DefaultValue != col.DefaultValue)
                sb.AppendLine(string.IsNullOrEmpty(col.DefaultValue) ? $"{column} DROP DEFAULT;" : $"{column} SET DEFAULT {col.DefaultValue};");
            if (generation.HasFlag(ColumnGenerationChange.AddIdentity))
                sb.AppendLine($"{column} ADD{Identity(col)};");
            // SET START WITH only records the start for a later RESTART; the sequence's current value is kept.
            var identity = new List<string>();
            if (generation.HasFlag(ColumnGenerationChange.IdentityKind)) identity.Add($"SET GENERATED {Kind(col)}");
            if (generation.HasFlag(ColumnGenerationChange.IdentityIncrement)) identity.Add($"SET INCREMENT BY {col.IdentityIncrement ?? "1"}");
            if (generation.HasFlag(ColumnGenerationChange.IdentitySeed)) identity.Add($"SET START WITH {col.IdentitySeed ?? "1"}");
            if (identity.Count > 0) sb.AppendLine($"{column} {string.Join(" ", identity)};");
            if (generation.HasFlag(ColumnGenerationChange.Expression))
                expressions.AppendLine($"{column} SET EXPRESSION AS ({col.GeneratedExpression});");
        }

        // New columns go after drops and alters: a dropped or renamed name is free by then, and a new generated
        // column reads the altered types. Changed generation expressions go last, as they may read new columns.
        foreach (var col in modified.Where(c => c.IsNew && !c.IsDropped))
            sb.AppendLine($"ALTER TABLE {table} ADD {BuildColumnString(col)};");
        sb.Append(expressions);

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
        var sql = $"CREATE {unique}INDEX {Quote(index.Name)} ON {Table(index.TableName)} ({cols})";
        if (!string.IsNullOrWhiteSpace(index.WherePredicate))
            sql += $" WHERE {index.WherePredicate}";
        return sql + ";";
    }

    public string GenerateDropIndex(string indexName, string? tableName = null) =>
        $"DROP INDEX {Table(indexName)};";

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

    public IReadOnlyList<string> GenerateCommentChanges(string tableName, IReadOnlyList<CommentChange> changes) =>
        changes.Select(c =>
        {
            var target = c.Column == null ? $"TABLE {Table(tableName)}" : $"COLUMN {Table(tableName)}.{Quote(c.Column)}";
            return $"COMMENT ON {target} IS {(c.Comment == null ? "NULL" : DdlColumnRules.Literal(c.Comment))};";
        }).ToList();

    public string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded) =>
        DdlColumnRules.TypeChangeLoss(original, modified, loaded);

    /// <summary>A referenced table, qualified as far as its identity says.</summary>
    private string Referenced(DatabaseObjectIdentity identity) => DdlColumnRules.QualifiedTable(identity, Quote);

    private string BuildColumnString(ColumnDefinition col)
    {
        var sb = new StringBuilder();
        sb.Append($"{Quote(col.Name)} {col.DataType}{DdlColumnRules.Collate(col)}");
        if (col.IdentityKind != null) sb.Append(Identity(col));
        sb.Append(DdlColumnRules.GeneratedClause(col));
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (!string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        return sb.ToString();
    }

    private static string Kind(ColumnDefinition col) => col.IdentityKind == TableDesignIdentityKind.Always ? "ALWAYS" : "BY DEFAULT";

    /// <summary>" GENERATED ALWAYS|BY DEFAULT AS IDENTITY", with the seed and increment when set.</summary>
    private static string Identity(ColumnDefinition col)
    {
        var options = new List<string>();
        if (col.IdentitySeed != null) options.Add($"START WITH {col.IdentitySeed}");
        if (col.IdentityIncrement != null) options.Add($"INCREMENT BY {col.IdentityIncrement}");
        return $" GENERATED {Kind(col)} AS IDENTITY" + (options.Count > 0 ? $" ({string.Join(" ", options)})" : "");
    }

    private static bool IsChanged(ColumnDefinition a, ColumnDefinition b) =>
        a.Name != b.Name || a.DataType != b.DataType || a.Collation != b.Collation || a.IsNullable != b.IsNullable
        || a.DefaultValue != b.DefaultValue || DdlColumnRules.GenerationChanged(a, b);
}
