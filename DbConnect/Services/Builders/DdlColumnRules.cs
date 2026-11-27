using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>Shared rules and clauses for the dialect builders.</summary>
internal static class DdlColumnRules
{
    /// <summary>
    /// For engines whose ALTER restates the whole column (MODIFY / ALTER COLUMN): restating it
    /// from the designer's fields drops whatever the loaded column has beyond them.
    /// </summary>
    public static string? RestatedColumnLoss(TableDesignColumn loaded)
    {
        var kept = Extras(loaded);
        return kept.Count == 0 ? null
            : $"changing it restates the whole column, which would drop its {string.Join(", ", kept)}; the engine's ALTER cannot restate them.";
    }

    /// <summary>
    /// For engines that alter one attribute at a time: only a type change (which restates the
    /// collation with it) can reset the rest.
    /// </summary>
    public static string? TypeChangeLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded)
    {
        if (!TypeOrCollationChanged(original, modified)) return null;
        var kept = Extras(loaded);
        return kept.Count == 0 ? null
            : $"changing its type could reset its {string.Join(", ", kept)}; the designer does not change the type of such a column.";
    }

    public static bool TypeOrCollationChanged(ColumnDefinition original, ColumnDefinition modified) =>
        !string.Equals(original.DataType, modified.DataType, StringComparison.Ordinal)
        || !string.Equals(original.Collation, modified.Collation, StringComparison.Ordinal);

    /// <summary>A COLLATE clause with a leading space, or empty for the type's default collation.</summary>
    public static string Collate(ColumnDefinition column) =>
        string.IsNullOrWhiteSpace(column.Collation) ? "" : $" COLLATE {column.Collation.Trim()}";

    // The collation is not listed: every builder restates it from the designer's Collation field.
    private static List<string> Extras(TableDesignColumn loaded)
    {
        var extras = new List<string>();
        if (loaded.Generated != null) extras.Add("generation expression");
        if (loaded.Identity != null) extras.Add("identity");
        // ALTER TYPE leaves an owned sequence at its old type, so a serial column keeps its type.
        if (loaded.Sequence is { Owned: true } sequence) extras.Add($"owned sequence {sequence.Name}");
        if (loaded.Properties.Count > 0) extras.Add(string.Join(", ", loaded.Properties));
        return extras;
    }

    /// <summary>A " GENERATED ALWAYS AS (expression) STORED|VIRTUAL" clause, or empty for an ordinary column.</summary>
    public static string GeneratedClause(ColumnDefinition column) =>
        column.GeneratedExpression is { } expression
            ? $" GENERATED ALWAYS AS ({expression}) {(column.GeneratedStorage ?? TableDesignGeneratedStorage.Virtual).ToString().ToUpperInvariant()}"
            : "";

    /// <summary>True when the column's identity or generation settings differ.</summary>
    public static bool GenerationChanged(ColumnDefinition a, ColumnDefinition b) => a.Generation != b.Generation;

    /// <summary>The primary key the columns' flags declare, in column order: how callers without key edits create tables.</summary>
    public static IReadOnlyList<TableKeySpec> KeysFromColumns(IEnumerable<ColumnDefinition> columns) =>
        columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList() is { Count: > 0 } members
            ? [new TableKeySpec(KeyKind.Primary, null, members)]
            : [];

    /// <summary>A "[CONSTRAINT name] PRIMARY KEY|UNIQUE [method] (columns)" clause.</summary>
    public static string KeyClause(TableKeySpec key, Func<string, string> quote, bool named = true) =>
        (named && key.Name != null ? $"CONSTRAINT {quote(key.Name)} " : "") +
        (key.Kind == KeyKind.Primary ? "PRIMARY KEY" : "UNIQUE") + (key.Method != null ? " " + key.Method : "") +
        $" ({string.Join(", ", key.Columns.Select(quote))})";

    /// <summary>True when the change only gives the key a new name.</summary>
    public static bool IsRename(TableKeyChange change) =>
        change is { From: { } from, To: { Name: not null } to } && from.Kind == to.Kind && from.Columns.SequenceEqual(to.Columns)
        && from.Name != to.Name;

    /// <summary>Single-column foreign keys from callers without the designer's structured ones; their table names are unqualified.</summary>
    public static IReadOnlyList<TableForeignKeySpec> Specs(IEnumerable<ForeignKeyDefinition>? foreignKeys) =>
        (foreignKeys ?? []).Select(fk => new TableForeignKeySpec(string.IsNullOrWhiteSpace(fk.ConstraintName) ? null : fk.ConstraintName,
            [new TableDesignColumnPair(fk.Column, fk.ReferencedColumn)],
            new DatabaseObjectIdentity(null, null, fk.ReferencedTable, SchemaNodeType.Table), fk.OnDelete, fk.OnUpdate)).ToList();

    /// <summary>A "[CONSTRAINT name] FOREIGN KEY (...) REFERENCES table (...) ON DELETE ... ON UPDATE ..." clause.</summary>
    public static string ForeignKeyClause(TableForeignKeySpec fk, Func<string, string> quote, Func<DatabaseObjectIdentity, string> table) =>
        (fk.Name != null ? $"CONSTRAINT {quote(fk.Name)} " : "") +
        $"FOREIGN KEY ({string.Join(", ", fk.Columns.Select(quote))}) REFERENCES {table(fk.ReferencedTable)} " +
        $"({string.Join(", ", fk.ReferencedColumns.Select(quote))}) ON DELETE {fk.OnDelete} ON UPDATE {fk.OnUpdate}";

    /// <summary>A "[CONSTRAINT name] CHECK (expression)" clause.</summary>
    public static string CheckClause(TableCheckSpec check, Func<string, string> quote) =>
        (check.Name != null ? $"CONSTRAINT {quote(check.Name)} " : "") + $"CHECK ({check.Expression})";

    /// <summary>True when the change only gives the check a new name.</summary>
    public static bool IsRename(TableCheckChange change) =>
        change is { From: { } loaded, To: { Name: not null } renamed } && loaded.Name != renamed.Name && loaded.Expression == renamed.Expression;

    /// <summary>The keys and foreign keys of callers without the designer's constraint edits, and no checks.</summary>
    public static TableConstraints ConstraintsFrom(IList<ColumnDefinition> columns, IEnumerable<ForeignKeyDefinition>? foreignKeys) =>
        new(KeysFromColumns(columns), Specs(foreignKeys), []);

    /// <summary>A referenced table qualified by its schema, or by its database where the engine has no schemas (MySQL).</summary>
    public static string QualifiedTable(DatabaseObjectIdentity table, Func<string, string> quote, bool byDatabase = false) =>
        (byDatabase ? table.Catalog : table.Schema) is { } owner ? $"{quote(owner)}.{quote(table.Name)}" : quote(table.Name);

    /// <summary>True when the change only gives the index a new name.</summary>
    public static bool IsRename(TableIndexChange change) =>
        change is { From: { } from, To: { } to } && from.Name != to.Name && from.IsUnique == to.IsUnique && from.Parts.SequenceEqual(to.Parts);

    /// <summary>True when the change only gives the foreign key a new name.</summary>
    public static bool IsRename(TableForeignKeyChange change)
    {
        if (change.From is not { } loaded || change.To is not { Name: not null } renamed || loaded.Name == renamed.Name) return false;
        return loaded.Pairs.SequenceEqual(renamed.Pairs)
               && TableIdentity.Same(loaded.ReferencedTable, renamed.ReferencedTable)
               && loaded.OnDelete.Equals(renamed.OnDelete, StringComparison.OrdinalIgnoreCase)
               && loaded.OnUpdate.Equals(renamed.OnUpdate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A FOREIGN KEY table constraint for CREATE TABLE.</summary>
    public static string ForeignKeyClause(ForeignKeyDefinition fk, Func<string, string> quote)
    {
        var name = string.IsNullOrWhiteSpace(fk.ConstraintName) ? "" : $"CONSTRAINT {quote(fk.ConstraintName)} ";
        return $"{name}FOREIGN KEY ({quote(fk.Column)}) REFERENCES {quote(fk.ReferencedTable)} ({quote(fk.ReferencedColumn)}) ON DELETE {fk.OnDelete} ON UPDATE {fk.OnUpdate}";
    }

    /// <summary>A standard SQL string literal ('' doubles a quote).</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>The original of a modified column: its own snapshot, or the original with the same name.</summary>
    public static ColumnDefinition? OriginalOf(ColumnDefinition column, IList<ColumnDefinition> original) =>
        column.Original ?? original.FirstOrDefault(o => o.Name == column.Name);
}
