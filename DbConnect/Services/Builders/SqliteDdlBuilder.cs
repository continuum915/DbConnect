using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

public sealed class SqliteDdlBuilder : IDdlBuilder
{
    public string ProviderName => "SQLite";

    public ColumnTypeRules TypeRules => ColumnTypeRules.Sqlite;

    public ColumnGenerationRules GenerationRules => ColumnGenerationRules.Sqlite;

    // Key changes are made by the rebuild (SqliteDefinitionSql.RebuildCreate), not by GenerateKeyChange.
    public TableKeyRules KeyRules => TableKeyRules.Sqlite;

    public TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.Sqlite;

    public TableCheckRules CheckRules => TableCheckRules.Sqlite;

    public TableIndexRules IndexRules => TableIndexRules.Sqlite;

    /// <summary>SQLite has no index rename, so a renamed index is dropped and created again.</summary>
    public IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change)
    {
        var statements = new List<string>();
        if (change.From is { } from) statements.Add($"DROP INDEX {Quote(from.Name)};");
        if (change.To is { } to)
            statements.Add(DdlIndexSql.Create(to, Quote(tableName), Quote));
        return statements;
    }

    public string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>The rebuild and its stored index and trigger SQL address tables by bare name, which reaches only main.</summary>
    public IDdlBuilder InSchema(string? owner) =>
        owner == null || owner.Equals("main", StringComparison.OrdinalIgnoreCase)
            ? this
            : throw new NotSupportedException(
                $"The designer can change SQLite tables only in the main database; this table is in {owner}.");

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns) =>
        GenerateCreateTable(tableName, columns, []);

    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, IList<ForeignKeyDefinition> foreignKeys) =>
        GenerateCreateTable(tableName, columns, DdlColumnRules.ConstraintsFrom(columns, foreignKeys));

    /// <summary>
    /// SQLite cannot add keys or foreign keys later, so they are all declared here. An unnamed
    /// single-column primary key stays on its column, where it makes an INTEGER column the rowid and can carry AUTOINCREMENT.
    /// </summary>
    public string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, TableConstraints constraints)
    {
        var (keys, foreignKeys, checks) = (constraints.Keys, constraints.ForeignKeys, constraints.Checks);
        var inline = keys.FirstOrDefault(k => k is { Kind: KeyKind.Primary, Name: null, Columns.Count: 1 });
        var lines = columns.Select(c => BuildColumnString(c, inline != null && inline.Columns[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        lines.AddRange(keys.Where(k => k != inline).Select(k => DdlColumnRules.KeyClause(k, Quote)));
        // SQLite references tables of the same database by bare name.
        lines.AddRange(foreignKeys.Select(fk => DdlColumnRules.ForeignKeyClause(fk, Quote, t => Quote(t.Name))));
        lines.AddRange(checks.Select(c => DdlColumnRules.CheckClause(c, Quote)));

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Quote(tableName)} (");
        sb.AppendLine(string.Join("," + Environment.NewLine, lines.Select(l => "    " + l)));
        sb.Append(");");
        return sb.ToString();
    }

    public string GenerateAlterTable(string tableName, IList<ColumnDefinition> original, IList<ColumnDefinition> modified, IList<IndexDefinition>? indexes = null, TableRebuildDefinition? definition = null)
    {
        var droppedColumnNames = original.Where(c => !modified.Any(m => !m.IsDropped && m.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedByDrop = indexes?.Where(idx => idx.Columns.Any(droppedColumnNames.Contains)
            || (idx.CreateSql != null && SqliteConstraintDependencies.ReferencesAny(
                string.Join(",", SqliteDefinitionSql.BodyTerms(idx.CreateSql)) + " " + idx.WherePredicate, droppedColumnNames))).ToList()
            ?? new List<IndexDefinition>();
        var indexesToRecreate = indexes?.Where(idx => !affectedByDrop.Contains(idx)).ToList()
            ?? new List<IndexDefinition>();
        var removedObjects = affectedByDrop.Select(idx => $"INDEX {idx.Name}").ToList();
        var schemaObjects = definition?.SchemaObjects ?? [];
        var removedSchema = schemaObjects.Where(o => o.ReferencedColumns.Any(droppedColumnNames.Contains))
            .ToHashSet();
        // Removing a view also removes its consumers and any INSTEAD OF triggers it owns.
        while (true)
        {
            var added = schemaObjects.Where(o => !removedSchema.Contains(o) && removedSchema.Any(r =>
                o.ReferencedObjects.Contains(r.Name) || o.TableName.Equals(r.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (added.Count == 0) break;
            removedSchema.UnionWith(added);
        }
        removedObjects.AddRange(removedSchema.Select(o => $"{o.Type.ToUpperInvariant()} {o.Name}"));

        var tempName = $"_rain_tmp_{tableName}";
        for (var suffix = 1; definition != null && new[] { tempName, tempName + "_fk_guard",
            tempName + "_fk_guard_trigger", tempName + "_sequence" }.Any(definition.ObjectNames.Contains); suffix++)
            tempName = $"_rain_tmp_{tableName}_{suffix}";
        var keepCols = modified.Where(c => !c.IsDropped).ToList();
        var createTable = definition == null
            ? GenerateCreateTable(tempName, keepCols)
            : SqliteDefinitionSql.RebuildCreate(definition.CreateSql, tempName, original, modified,
                c => BuildColumnString(c, false), Quote, droppedColumnNames, removedObjects, definition.KeyChanges, definition.ForeignKeyChanges,
                definition.CheckChanges);
        var sb = new StringBuilder();
        sb.AppendLine(SqliteRebuildExecution.Marker);
        if (removedObjects.Count > 0)
        {
            sb.AppendLine("-- The following objects reference dropped columns and will not be recreated:");
            foreach (var item in removedObjects)
                sb.AppendLine($"--   {item.Replace("\r", " ").Replace("\n", " ")}");
        }

        sb.AppendLine("-- SQLite rebuild");
        sb.AppendLine("PRAGMA foreign_keys=OFF;");
        sb.AppendLine("BEGIN TRANSACTION;");

        var sequence = Quote(tempName + "_sequence");
        var preserveSequence = definition != null && SqliteDefinitionSql.HasAutoincrement(definition.CreateSql);
        var tableLiteral = "'" + tableName.Replace("'", "''") + "'";
        if (preserveSequence)
            sb.AppendLine($"CREATE TEMP TABLE {sequence} AS SELECT seq FROM sqlite_sequence WHERE name={tableLiteral};");
        sb.AppendLine(createTable);

        // Generated columns of the new table compute their values; a column that stops being generated keeps the values it had.
        var survivingCols = modified.Where(c => !c.IsNew && !c.IsDropped && c.GeneratedExpression == null
            && (c.Loaded != null || definition?.GeneratedColumns.Contains(c.Name) != true)).ToList();
        var colsStr = string.Join(", ", survivingCols.Select(c => Quote(c.Name)));
        sb.AppendLine($"INSERT INTO {Quote(tempName)} ({colsStr}) SELECT {colsStr} FROM {Quote(tableName)};");
        foreach (var view in schemaObjects.Where(o => o.Type == "view"))
            sb.AppendLine($"DROP VIEW {Quote(view.Name)};");
        foreach (var item in schemaObjects.Where(o => o.Type == "trigger"
            && !o.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)
            && !schemaObjects.Any(v => v.Type == "view" && v.Name.Equals(o.TableName, StringComparison.OrdinalIgnoreCase))))
            sb.AppendLine($"DROP TRIGGER {Quote(item.Name)};");
        sb.AppendLine($"DROP TABLE {Quote(tableName)};");
        sb.AppendLine($"ALTER TABLE {Quote(tempName)} RENAME TO {Quote(tableName)};");
        if (preserveSequence)
        {
            sb.AppendLine($"UPDATE sqlite_sequence SET seq=max(seq, coalesce((SELECT seq FROM {sequence}), seq)) WHERE name={tableLiteral};");
            sb.AppendLine($"DROP TABLE {sequence};");
        }

        foreach (var idx in indexesToRecreate)
        {
            if (idx.IsDropped) continue;
            if (!string.IsNullOrWhiteSpace(idx.CreateSql))
                sb.AppendLine(idx.CreateSql.TrimEnd(';') + ";");
            else if (!idx.Name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine(GenerateCreateIndex(idx));
        }

        foreach (var item in schemaObjects.Where(o => !removedSchema.Contains(o)).OrderBy(o => o.Type == "view" ? 0 : 1))
            sb.AppendLine(item.CreateSql.TrimEnd(';') + ";");

        // A SQL guard makes the preview itself enforce rollback, even outside DbConnect.
        var guard = Quote(tempName + "_fk_guard");
        var trigger = Quote(tempName + "_fk_guard_trigger");
        sb.AppendLine($"CREATE TEMP TABLE {guard} (violations INTEGER);");
        sb.AppendLine($"CREATE TEMP TRIGGER {trigger} BEFORE INSERT ON {guard} WHEN NEW.violations > 0 BEGIN");
        sb.AppendLine("    SELECT RAISE(ROLLBACK, 'SQLite rebuild failed: foreign key integrity check reported violations');");
        sb.AppendLine("END;");
        sb.AppendLine($"INSERT INTO {guard} SELECT count(*) FROM pragma_foreign_key_check;");
        sb.AppendLine($"DROP TABLE {guard};");

        sb.AppendLine("COMMIT;");
        sb.AppendLine("PRAGMA foreign_keys=ON;");
        return sb.ToString();
    }

    public string GenerateCreateIndex(IndexDefinition index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var cols = string.Join(", ", index.Columns.Select((c, i) =>
        {
            var dir = i < index.ColumnDirections.Count ? index.ColumnDirections[i] : IndexColumnDirection.Asc;
            return $"{Quote(c)} {(dir == IndexColumnDirection.Desc ? "DESC" : "ASC")}";
        }));
        var sql = $"CREATE {unique}INDEX {Quote(index.Name)} ON {Quote(index.TableName)} ({cols})";
        if (!string.IsNullOrWhiteSpace(index.WherePredicate))
            sql += $" WHERE {index.WherePredicate}";
        return sql + ";";
    }

    public string GenerateDropIndex(string indexName, string? tableName = null) =>
        $"DROP INDEX {Quote(indexName)};";

    public string GenerateCreateView(string viewName, string selectSql, bool createOrReplace = false) =>
        $"CREATE VIEW {Quote(viewName)} AS{Environment.NewLine}{selectSql};";

    public string GenerateAddUniqueConstraint(string tableName, string constraintName, IList<string> columns)
    {
        var cols = string.Join(", ", columns.Select(Quote));
        return $"CREATE UNIQUE INDEX {Quote(constraintName)} ON {Quote(tableName)} ({cols});";
    }

    public string GenerateAddForeignKey(ForeignKeyDefinition fk) =>
        "-- ⚠️ SQLite does not support ADD CONSTRAINT via ALTER TABLE. Rebuild the table to add foreign keys.";

    public string GenerateDropConstraint(string tableName, string constraintName) =>
        "-- SQLite does not support DROP CONSTRAINT. Recreate the table to remove constraints.";

    private string BuildColumnString(ColumnDefinition col, bool primaryKey)
    {
        var sb = new StringBuilder();
        sb.Append($"{Quote(col.Name)} {col.DataType}{DdlColumnRules.Collate(col)}");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (primaryKey) sb.Append(col.IdentityKind == TableDesignIdentityKind.AutoIncrement ? " PRIMARY KEY AUTOINCREMENT" : " PRIMARY KEY");
        sb.Append(DdlColumnRules.GeneratedClause(col));
        if (!string.IsNullOrEmpty(col.DefaultValue)) sb.Append($" DEFAULT {col.DefaultValue}");
        return sb.ToString();
    }
}
