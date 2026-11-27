using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed class GenericDdlBuilder : IDdlBuilder
{
    private readonly string? _owner;

    public GenericDdlBuilder(string providerName, string? owner = null)
    {
        ProviderName = providerName;
        _owner = owner;
    }

    public string ProviderName { get; }

    public IDdlBuilder InSchema(string? owner) => new GenericDdlBuilder(ProviderName, owner);

    private string Table(string name) => _owner == null ? Quote(name) : $"{Quote(_owner)}.{Quote(name)}";

    public string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded) =>
        DdlColumnRules.RestatedColumnLoss(loaded);

    public string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

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

        foreach (var col in modified.Where(c => c.IsDropped && !c.IsNew))
            sb.AppendLine($"ALTER TABLE {Table(tableName)} DROP COLUMN {Quote((DdlColumnRules.OriginalOf(col, original) ?? col).Name)};");

        foreach (var col in modified.Where(c => !c.IsNew && !c.IsDropped))
        {
            var orig = DdlColumnRules.OriginalOf(col, original);
            if (orig != null && IsChanged(orig, col))
                sb.AppendLine($"ALTER TABLE {Table(tableName)} ALTER COLUMN {BuildColumnString(col)};");
        }

        // New columns go last: a dropped or renamed name is free by then, and a new generated column reads altered types.
        foreach (var col in modified.Where(c => c.IsNew && !c.IsDropped))
            sb.AppendLine($"ALTER TABLE {Table(tableName)} ADD {BuildColumnString(col)};");

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
        $"DROP INDEX {Quote(indexName)};";

    public string GenerateCreateView(string viewName, string selectSql, bool createOrReplace = false) =>
        $"CREATE VIEW {Quote(viewName)} AS{Environment.NewLine}{selectSql};";

    public string GenerateAddUniqueConstraint(string tableName, string constraintName, IList<string> columns)
    {
        var cols = string.Join(", ", columns.Select(Quote));
        return $"ALTER TABLE {Table(tableName)} ADD CONSTRAINT {Quote(constraintName)} UNIQUE ({cols});";
    }

    public string GenerateAddForeignKey(ForeignKeyDefinition fk) =>
        $"ALTER TABLE {Table(fk.Table)} ADD {DdlColumnRules.ForeignKeyClause(fk, Quote)};";

    public string GenerateDropConstraint(string tableName, string constraintName) =>
        $"ALTER TABLE {Table(tableName)} DROP CONSTRAINT {Quote(constraintName)};";

    /// <summary>A referenced table, qualified as far as its identity says.</summary>
    private string Referenced(DatabaseObjectIdentity identity) => Quote(identity.Name);

    private string BuildColumnString(ColumnDefinition col)
    {
        var sb = new StringBuilder();
        sb.Append($"{Quote(col.Name)} {col.DataType}");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (!string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        return sb.ToString();
    }

    private static bool IsChanged(ColumnDefinition a, ColumnDefinition b) =>
        a.DataType != b.DataType || a.IsNullable != b.IsNullable || a.DefaultValue != b.DefaultValue;
}
