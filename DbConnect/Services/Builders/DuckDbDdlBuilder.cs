using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed class DuckDbDdlBuilder : IDdlBuilder
{
    private readonly string? _owner;

    public DuckDbDdlBuilder(string? owner = null) => _owner = owner;

    public string ProviderName => "DuckDB";

    public ColumnTypeRules TypeRules => ColumnTypeRules.DuckDb;

    public ColumnGenerationRules GenerationRules => ColumnGenerationRules.DuckDb;

    public TableKeyRules KeyRules => TableKeyRules.DuckDb;

    public TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.DuckDb;

    public TableCheckRules CheckRules => TableCheckRules.DuckDb;

    public TableIndexRules IndexRules => TableIndexRules.DuckDb;

    /// <summary>DuckDB has no index rename, so a renamed index is dropped and created again.</summary>
    public IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change)
    {
        var statements = new List<string>();
        if (change.From is { } from) statements.Add($"DROP INDEX {Table(from.Name)};");
        if (change.To is { } to)
            statements.Add(DdlIndexSql.Create(to, Table(tableName), Quote));
        return statements;
    }

    public IDdlBuilder InSchema(string? owner) => new DuckDbDdlBuilder(owner);

    public string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    // Tables and indexes both live in the table's schema.
    private string Table(string name) => _owner == null ? Quote(name) : $"{Quote(_owner)}.{Quote(name)}";

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns) =>
        GenerateCreateTable(tableName, columns, []);

    // DuckDB cannot add foreign keys later, so they are declared here.
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
            if (orig.Name != col.Name)
                sb.AppendLine($"ALTER TABLE {table} RENAME COLUMN {Quote(orig.Name)} TO {Quote(col.Name)};");
            // A collation is set with the type; TYPE without COLLATE returns the column to the type's default.
            if (DdlColumnRules.TypeOrCollationChanged(orig, col))
                sb.AppendLine($"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)} TYPE {col.DataType}{DdlColumnRules.Collate(col)};");
            if (orig.IsNullable != col.IsNullable)
                sb.AppendLine(col.IsNullable
                    ? $"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)} DROP NOT NULL;"
                    : $"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)} SET NOT NULL;");
            if (orig.DefaultValue != col.DefaultValue)
                sb.AppendLine(string.IsNullOrEmpty(col.DefaultValue)
                    ? $"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)} DROP DEFAULT;"
                    : $"ALTER TABLE {table} ALTER COLUMN {Quote(col.Name)} SET DEFAULT {col.DefaultValue};");
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
        "-- DuckDB cannot add a foreign key to an existing table; foreign keys must be declared when the table is created.";

    public string GenerateDropConstraint(string tableName, string constraintName) =>
        $"ALTER TABLE {Table(tableName)} DROP CONSTRAINT {Quote(constraintName)};";

    public string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded) =>
        DdlColumnRules.TypeChangeLoss(original, modified, loaded);

    /// <summary>A referenced table, qualified as far as its identity says.</summary>
    private string Referenced(DatabaseObjectIdentity identity) => DdlColumnRules.QualifiedTable(identity, Quote);

    private string BuildColumnString(ColumnDefinition col)
    {
        var sb = new StringBuilder();
        // DuckDB has no identity syntax; GenerationRules offers none, so validation rejects one before it gets here.
        sb.Append($"{Quote(col.Name)} {col.DataType}{DdlColumnRules.Collate(col)}{DdlColumnRules.GeneratedClause(col)}");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (!string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        return sb.ToString();
    }

    private static bool IsChanged(ColumnDefinition a, ColumnDefinition b) =>
        a.Name != b.Name || a.DataType != b.DataType || a.Collation != b.Collation || a.IsNullable != b.IsNullable
        || a.DefaultValue != b.DefaultValue || DdlColumnRules.GenerationChanged(a, b);
}
