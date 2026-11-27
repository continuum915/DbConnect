using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>What the designer knows about a referenced table: its columns and the column sets of its keys.</summary>
public sealed record ForeignKeyTarget(MetadataLoadState State, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Keys,
    string? Error = null)
{
    public static ForeignKeyTarget Loading { get; } = new(MetadataLoadState.Loading, [], []);

    public static ForeignKeyTarget Failed(string error) => new(MetadataLoadState.Failed, [], [], error);

    /// <summary>The target as its loaded design describes it: its primary key, unique keys and unfiltered unique indexes.</summary>
    public static ForeignKeyTarget From(TableDesign design) => new(MetadataLoadState.Loaded,
        design.Columns.Select(c => c.Name).ToList(),
        new[] { design.PrimaryKey?.Columns }.OfType<IReadOnlyList<string>>()
            .Concat(design.UniqueKeys.Select(k => k.Columns))
            .Concat(design.Indexes.Where(i => i.IsUnique && i.Filter == null && i.Parts.All(p => p.Column != null))
                .Select(i => (IReadOnlyList<string>)i.Parts.Select(p => p.Column!).ToList()))
            .ToList());
}

/// <summary>
/// Foreign keys in the designer's change plan: which are added, dropped, renamed or re-created, which
/// changes are blocked or need review, and which definitions are mistakes. Referenced columns and key
/// compatibility are checked against the referenced table's loaded design, or the table itself.
/// </summary>
internal static class TableForeignKeyPlanning
{
    public static IReadOnlyList<TableForeignKeySpec> CreateForeignKeys(TableDesignerEdits e) =>
        e.ForeignKeys.Where(k => !k.IsDropped).Select(k => k.Spec()).OfType<TableForeignKeySpec>().ToList();

    /// <summary>
    /// The changes to the loaded foreign keys and the added ones. An unchanged foreign key over a column being
    /// dropped is dropped first on engines that alter in place (SQL Server and MySQL refuse the column otherwise);
    /// a rebuild leaves it out as before.
    /// </summary>
    public static List<(EditableForeignKey Key, TableForeignKeyChange Change)> Changes(TableDesignerEdits e, TableForeignKeyRules rules)
    {
        var dropped = TableKeyPlanning.DroppedColumns(e);
        var changes = new List<(EditableForeignKey, TableForeignKeyChange)>();
        foreach (var key in e.ForeignKeys)
        {
            if (key.IsNew)
            {
                if (!key.IsDropped && key.Spec() is { } added) changes.Add((key, new(null, added)));
                continue;
            }
            var loaded = Loaded(key);
            if (key.IsDropped || !e.RebuildsColumns && !key.DefinitionChanged && key.Pairs.Any(p => dropped.Contains(p.Column.Trim())))
                changes.Add((key, new(loaded, null)));
            else if ((key.DefinitionChanged || key.Renamed && !rules.Renames) && key.Spec() is { } changed)
                changes.Add((key, new(loaded, changed)));
            else if (key.Renamed) changes.Add((key, new(loaded, loaded with { Name = key.Spec()!.Name })));
        }
        return changes;
    }

    private static TableForeignKeySpec Loaded(EditableForeignKey key)
    {
        var loaded = key.Loaded!;
        return new(loaded.Name, loaded.Columns, loaded.ReferencedTable, loaded.OnDelete, loaded.OnUpdate) { GeneratedName = loaded.GeneratedName };
    }

    /// <summary>Why a change cannot be made safely; empty when it can.</summary>
    public static IEnumerable<string> Blocked(EditableForeignKey key, TableForeignKeyChange change, TableDesign baseline, bool inPlaceRename)
    {
        var label = Label(key);
        if (baseline.UnsupportedReason(TableDesignFeature.ConstraintChanges) is { } unsupported)
        {
            yield return $"{label} cannot be changed: changing foreign keys after creation is {unsupported}.";
            yield break;
        }
        // A rename in place keeps everything; a re-creation (MySQL renames that way) or a rebuild restates only what the designer edits.
        if (change is { From: not null, To: not null } && !inPlaceRename && key.Loaded is { } loaded)
        {
            var lost = new[] { loaded.State, loaded.Comment != null ? "comment" : null }.OfType<string>().ToList();
            if (lost.Count > 0) yield return $"{label} cannot be re-created without losing its {string.Join(", ", lost)}.";
        }
    }

    /// <summary>Foreign-key changes Save may only run once confirmed.</summary>
    /// <remarks>A rename keeps the definition, so rows that met it still do, however the engine renames.</remarks>
    public static IEnumerable<string> Destructive(EditableForeignKey key, TableForeignKeyChange change)
    {
        if (change.From is not { } from || DdlColumnRules.IsRename(change)) yield break;
        if (change.To == null)
            yield return $"Drops {Label(key)}; ({string.Join(", ", from.Columns)}) are no longer checked against " +
                         $"{TableDesignText.Qualified(from.ReferencedTable)}.";
        else
            yield return $"Re-creates {Label(key)} as ({string.Join(", ", change.To.Columns)}) -> " +
                         $"{TableDesignText.Qualified(change.To.ReferencedTable)} ({string.Join(", ", change.To.ReferencedColumns)}) " +
                         $"ON DELETE {change.To.OnDelete} ON UPDATE {change.To.OnUpdate}; existing rows are checked again and the save fails if any violate it.";
    }

    /// <summary>Mistakes in the foreign-key definitions. Unchanged loaded foreign keys are only checked for name clashes.</summary>
    public static IEnumerable<string> Problems(TableDesignerEdits e, TableForeignKeyRules rules)
    {
        var columns = e.Columns.Where(c => !c.IsDropped).GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var active = e.ForeignKeys.Where(k => !k.IsDropped).ToList();
        foreach (var name in active.Select(k => k.Spec()?.Name).OfType<string>().GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Duplicate foreign key constraint name: \"{name.Key}\".";
        foreach (var key in active.Where(k => k.IsNew || k.DefinitionChanged))
            foreach (var problem in KeyProblems(key, e, rules, columns)) yield return problem;
    }

    private static IEnumerable<string> KeyProblems(EditableForeignKey key, TableDesignerEdits e, TableForeignKeyRules rules,
        Dictionary<string, ColumnDefinition> columns)
    {
        var label = Label(key);
        if (key.ReferencedTable is not { } target)
        {
            yield return $"{label} has no referenced table.";
            yield break;
        }
        if (key.Pairs.Count == 0) yield return $"{label} has no column pairs.";
        if (key.Pairs.Any(p => string.IsNullOrWhiteSpace(p.Column) || string.IsNullOrWhiteSpace(p.ReferencedColumn)))
        {
            yield return $"{label} has an incomplete column pair.";
            yield break;
        }
        var locals = key.Pairs.Select(p => p.Column.Trim()).ToList();
        var referenced = key.Pairs.Select(p => p.ReferencedColumn.Trim()).ToList();
        foreach (var column in locals.Where(c => !columns.ContainsKey(c)))
            yield return $"{label} refers to column \"{column}\", which the table does not have.";
        foreach (var column in Repeated(locals)) yield return $"{label} lists column \"{column}\" more than once.";
        foreach (var column in Repeated(referenced)) yield return $"{label} references column \"{column}\" more than once.";
        foreach (var action in new[] { ("ON DELETE", key.OnDelete), ("ON UPDATE", key.OnUpdate) }
                     .Where(a => !rules.Actions.Contains(a.Item2, StringComparer.OrdinalIgnoreCase)))
            yield return $"{rules.Provider} does not support {action.Item1} {action.Item2} ({label}).";
        if (key.OnDelete.Equals("SET NULL", StringComparison.OrdinalIgnoreCase) || key.OnUpdate.Equals("SET NULL", StringComparison.OrdinalIgnoreCase))
            foreach (var column in locals.Where(c => columns.TryGetValue(c, out var definition) && !definition.IsNullable))
                yield return $"{label} sets column \"{column}\" to NULL, but it is Not Null.";
        if (rules.SameSchemaOnly && e.Baseline is { } baseline && target.Schema != null && baseline.Table.Schema != null
            && !target.Schema.Equals(baseline.Table.Schema, StringComparison.OrdinalIgnoreCase))
            yield return $"{label}: {rules.Provider} foreign keys reference tables in the same database.";

        var metadata = IsSelf(target, e) ? Self(e) : e.ForeignKeyTarget(target);
        var targetName = TableDesignText.Qualified(target);
        switch (metadata?.State)
        {
            case null or MetadataLoadState.Loading or MetadataLoadState.NotLoaded:
                yield return $"{label}: the columns of {targetName} are still loading.";
                yield break;
            case MetadataLoadState.Failed:
                yield return $"{label}: the columns of {targetName} could not be read ({metadata.Error}); retry on the Foreign Keys tab.";
                yield break;
        }
        foreach (var column in referenced.Where(c => !metadata.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            yield return $"{label} references column \"{column}\", which {targetName} does not have.";
        if (!metadata.Keys.Any(k => rules.ReferencedKeyInOrder
                ? k.SequenceEqual(referenced, StringComparer.OrdinalIgnoreCase)
                : k.Count == referenced.Count && k.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(referenced)))
            yield return rules.ReferencedKeyInOrder && metadata.Keys.Any(k => k.Count == referenced.Count && k.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(referenced))
                ? $"{label}: {rules.Provider} needs the referenced columns in the key's order ({string.Join(", ", metadata.Keys.First(k => k.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(referenced)))})."
                : $"{label}: ({string.Join(", ", referenced)}) is not a primary or unique key of {targetName}.";
    }

    /// <summary>The referenced table is the table being designed.</summary>
    public static bool IsSelf(DatabaseObjectIdentity target, TableDesignerEdits e) =>
        e.Baseline is { } baseline ? TableIdentity.Same(target, baseline.Table) : target.Name == e.TableName && target.Schema == null;

    /// <summary>The table being designed as a target: its current columns and keys.</summary>
    private static ForeignKeyTarget Self(TableDesignerEdits e) => new(MetadataLoadState.Loaded,
        e.Columns.Where(c => !c.IsDropped).Select(c => c.Name).ToList(),
        TableKeyPlanning.CreateKeys(e, e.Columns.Where(c => !c.IsDropped)).Select(k => k.Columns).ToList());

    public static string Label(EditableForeignKey key) =>
        "Foreign key" + ((key.Spec()?.Name ?? key.Loaded?.Name) is { } name ? $" {name}"
            : $" ({string.Join(", ", key.Loaded?.Columns.Select(p => p.Column) ?? key.Pairs.Select(p => p.Column))})");

    private static IEnumerable<string> Repeated(IEnumerable<string> names) =>
        names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
}
