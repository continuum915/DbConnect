using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed class SqlServerDdlBuilder : IDdlBuilder
{
    private readonly string? _owner;

    public SqlServerDdlBuilder(string? owner = null) => _owner = owner;

    public string ProviderName => "SQL Server";

    public ColumnTypeRules TypeRules => ColumnTypeRules.SqlServer;

    public ColumnGenerationRules GenerationRules => ColumnGenerationRules.SqlServer;

    public TableKeyRules KeyRules => TableKeyRules.SqlServer;

    public TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.SqlServer;

    public IReadOnlyList<string> GenerateForeignKeyChange(string tableName, TableForeignKeyChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.ForeignKeyClause(to, Quote, Referenced) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    public TableCheckRules CheckRules => TableCheckRules.SqlServer;

    public IReadOnlyList<string> GenerateCheckChange(string tableName, TableCheckChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.CheckClause(to, Quote) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    /// <summary>Renames the constraint <paramref name="drop"/> names, or drops it and adds <paramref name="add"/>.</summary>
    private IReadOnlyList<string> ConstraintChange(string tableName, string? drop, string? add, string? renameTo)
    {
        var table = Table(tableName);
        if (renameTo != null) return [Rename(drop!, renameTo)];
        var statements = new List<string>();
        if (drop != null) statements.Add($"ALTER TABLE {table} DROP CONSTRAINT {Quote(drop)};");
        if (add != null) statements.Add($"ALTER TABLE {table} ADD {add};");
        return statements;
    }

    /// <summary>sp_rename of a constraint in the table's schema (the caller's default when the builder has none).</summary>
    private string Rename(string from, string to)
    {
        var schema = _owner == null ? "SCHEMA_NAME()" : Unicode(_owner);
        return $"DECLARE @rain_object nvarchar(600) = QUOTENAME({schema}) + N'.' + QUOTENAME({Unicode(from)}); " +
               $"EXEC sp_rename @rain_object, {Unicode(to)}, N'OBJECT';";
    }

    /// <summary>A key the engine named is addressed by its backing index, which has the constraint's name.</summary>
    public IReadOnlyList<string> GenerateKeyChange(string tableName, TableKeyChange change) =>
        ConstraintChange(tableName, change.From?.Address, change.To is { } to ? DdlColumnRules.KeyClause(to, Quote) : null,
            DdlColumnRules.IsRename(change) ? change.To!.Name : null);

    public TableIndexRules IndexRules => TableIndexRules.SqlServer;

    /// <summary>CLUSTERED or NONCLUSTERED is restated as loaded; a new index takes the default (NONCLUSTERED).</summary>
    public IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change)
    {
        var table = Table(tableName);
        if (DdlColumnRules.IsRename(change))
        {
            var schema = _owner == null ? "SCHEMA_NAME()" : Unicode(_owner);
            return [$"DECLARE @rain_object nvarchar(1000) = QUOTENAME({schema}) + N'.' + QUOTENAME({Unicode(tableName)}) + N'.' + " +
                    $"QUOTENAME({Unicode(change.From!.Name)}); EXEC sp_rename @rain_object, {Unicode(change.To!.Name)}, N'INDEX';"];
        }
        var statements = new List<string>();
        if (change.From is { } from) statements.Add($"DROP INDEX {Quote(from.Name)} ON {table};");
        if (change.To is { } to)
        {
            var kind = to.Method is { } method && IndexRules.KeptMethods.Contains(method) ? method.ToUpperInvariant() + " " : "";
            statements.Add(DdlIndexSql.Create(to, table, Quote, kind));
        }
        return statements;
    }

    public IDdlBuilder InSchema(string? owner) => new SqlServerDdlBuilder(owner);

    public string Quote(string identifier) =>
        $"[{identifier.Replace("]", "]]")}]";

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

        var lines = columns.Select(c => $"    {BuildColumnString(c, includeDefault: true)}").ToList();
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

        // ALTER COLUMN cannot carry a DEFAULT; defaults are separate constraints.
        foreach (var col in modified.Where(c => !c.IsNew && !c.IsDropped))
        {
            var orig = DdlColumnRules.OriginalOf(col, original);
            // ALTER COLUMN restates type, collation and nullability; NULL is explicit so it never follows a session setting.
            if (orig != null && (DdlColumnRules.TypeOrCollationChanged(orig, col) || orig.IsNullable != col.IsNullable))
                sb.AppendLine($"ALTER TABLE {table} ALTER COLUMN {BuildColumnString(col, includeDefault: false)}{(col.IsNullable ? " NULL" : "")};");
        }

        // New columns go last: a dropped or renamed name is free by then, and a new generated column reads altered types.
        foreach (var col in modified.Where(c => c.IsNew && !c.IsDropped))
            sb.AppendLine($"ALTER TABLE {table} ADD {BuildColumnString(col, includeDefault: true)};");

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
        if (index.IncludedColumns.Count > 0)
            sql += $" INCLUDE ({string.Join(", ", index.IncludedColumns.Select(Quote))})";
        return sql + ";";
    }

    public string GenerateDropIndex(string indexName, string? tableName = null)
    {
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentException("SQL Server requires a table name for DROP INDEX.", nameof(tableName));
        return $"DROP INDEX {Quote(indexName)} ON {Table(tableName)};";
    }

    public string GenerateCreateView(string viewName, string selectSql, bool createOrReplace = false)
    {
        var keyword = createOrReplace ? "CREATE OR ALTER VIEW" : "CREATE VIEW";
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

    /// <summary>
    /// Comments are MS_Description extended properties. Without an owner the table is in the
    /// caller's default schema, which only SCHEMA_NAME() knows, so each statement reads it into a
    /// variable first and stays one self-contained step.
    /// </summary>
    public IReadOnlyList<string> GenerateCommentChanges(string tableName, IReadOnlyList<CommentChange> changes)
    {
        var statements = new List<string>();
        var schema = _owner == null ? "@rain_schema" : Unicode(_owner);
        var declare = _owner == null ? "DECLARE @rain_schema sysname = SCHEMA_NAME(); " : "";
        foreach (var change in changes)
        {
            var procedure = change.Comment == null ? "sp_dropextendedproperty" : change.Existed ? "sp_updateextendedproperty" : "sp_addextendedproperty";
            if (change.Comment == null && !change.Existed) continue;
            var value = change.Comment == null ? "" : $", @value = {Unicode(change.Comment)}";
            var column = change.Column == null ? "" : $", @level2type = N'COLUMN', @level2name = {Unicode(change.Column)}";
            statements.Add($"{declare}EXEC sys.{procedure} @name = N'MS_Description'{value}, @level0type = N'SCHEMA', @level0name = {schema}, " +
                           $"@level1type = N'TABLE', @level1name = {Unicode(tableName)}{column};");
        }
        return statements;
    }

    public string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded)
    {
        if (!string.Equals(original.DefaultValue, modified.DefaultValue, StringComparison.Ordinal))
            return "changing a default on SQL Server replaces its default constraint, which is not available yet.";
        return !DdlColumnRules.TypeOrCollationChanged(original, modified) && original.IsNullable == modified.IsNullable
            ? null
            : DdlColumnRules.RestatedColumnLoss(loaded);
    }

    private static string Unicode(string value) => "N" + DdlColumnRules.Literal(value);

    /// <summary>A referenced table, qualified as far as its identity says.</summary>
    private string Referenced(DatabaseObjectIdentity identity) => DdlColumnRules.QualifiedTable(identity, Quote);

    private string BuildColumnString(ColumnDefinition col, bool includeDefault)
    {
        var sb = new StringBuilder();
        // A computed column takes its type from its expression.
        if (col.GeneratedExpression is { } expression)
            return $"{Quote(col.Name)} AS ({expression})" + (col.GeneratedStorage == TableDesignGeneratedStorage.Stored ? " PERSISTED" : "") +
                   (col.IsNullable ? "" : " NOT NULL");
        sb.Append($"{Quote(col.Name)} {col.DataType}{DdlColumnRules.Collate(col)}");
        if (col.IdentityKind != null)
            sb.Append(col.IdentitySeed != null || col.IdentityIncrement != null
                ? $" IDENTITY({col.IdentitySeed ?? "1"}, {col.IdentityIncrement ?? "1"})"
                : " IDENTITY");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (includeDefault && !string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        return sb.ToString();
    }
}
