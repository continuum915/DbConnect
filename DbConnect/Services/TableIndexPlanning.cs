using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Independent indexes in the designer's change plan: which are created, dropped, renamed or re-created,
/// which of those changes are blocked (by properties a re-created index would lose, or by foreign keys that
/// depend on it), which need review, and which index definitions are mistakes (<see cref="TableIndexValidation"/>). Indexes that back a key are
/// read-only here; the key's own changes re-create them.
/// </summary>
internal static class TableIndexPlanning
{
    /// <summary>The indexes CREATE TABLE is followed by.</summary>
    public static IEnumerable<TableIndexChange> CreateIndexes(TableDesignerEdits e) =>
        e.Indexes.Where(i => !i.IsDropped && !i.IsReadOnly).Select(i => new TableIndexChange(null, i.Spec()));

    /// <summary>
    /// The changes to the loaded independent indexes and the added ones, in list order. An unchanged index that
    /// uses a column being dropped (as a key, an included column, or in an expression or filter) is dropped first
    /// on engines that alter in place, so SQL Server does not refuse the column, MySQL does not silently shrink
    /// the index and PostgreSQL does not drop it unreviewed; a rebuild leaves it out as before.
    /// </summary>
    public static List<(EditableIndex Index, TableIndexChange Change)> Changes(TableDesignerEdits e, TableIndexRules rules)
    {
        var dropped = TableKeyPlanning.DroppedColumns(e);
        var changes = new List<(EditableIndex, TableIndexChange)>();
        foreach (var index in e.Indexes.Where(i => !i.IsReadOnly))
        {
            if (index.Loaded is not { } loaded)
            {
                if (!index.IsDropped) changes.Add((index, new(null, index.Spec())));
                continue;
            }
            var redefined = Redefined(index, e);
            if (index.IsDropped || !e.RebuildsColumns && !redefined && UsedColumns(loaded, dropped, e).Any())
                changes.Add((index, new(loaded, null)));
            else if (redefined || index.Renamed && !rules.Renames)
                changes.Add((index, new(loaded, index.Spec())));
            else if (index.Renamed) changes.Add((index, new(loaded, loaded with { Name = index.GivenName })));
        }
        return changes;
    }

    /// <summary>The dropped columns the index uses: as key parts, included columns, or in its expressions or filter.</summary>
    private static IEnumerable<string> UsedColumns(TableDesignIndex index, HashSet<string> dropped, TableDesignerEdits e) =>
        index.Parts.Select(p => p.Column).Concat(index.IncludedColumns).OfType<string>().Where(dropped.Contains)
            .Concat(index.Parts.Select(p => p.Expression).Append(index.Filter).OfType<string>()
                .SelectMany(text => TableCheckPlanning.References(text, dropped, e)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The index's uniqueness, key parts, included columns, filter or method differ from the loaded ones. A column is
    /// compared by its loaded name, so renaming a column (which the engine carries into its indexes) does not re-create it.
    /// </summary>
    public static bool Redefined(EditableIndex index, TableDesignerEdits e) =>
        index.Loaded is { } loaded && (index.IsUnique != loaded.IsUnique || index.SettingsEdited || !loaded.Parts.SequenceEqual(index.Parts.Select(p =>
        {
            var part = p.Part();
            return part.Column is { } column && e.Columns.FirstOrDefault(c => !c.IsNew && c.Original != null
                       && c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))?.Original!.Name is { } original
                ? part with { Column = original }
                : part;
        })));

    /// <summary>Why a change cannot be made safely; empty when it can.</summary>
    /// <remarks><paramref name="planned"/> gives each loaded index as the plan leaves it: re-created, or null when it drops it.</remarks>
    public static IEnumerable<string> Blocked(EditableIndex index, TableIndexChange change, TableDesign baseline, TableIndexRules rules,
        bool inPlaceRename, IReadOnlyList<TableForeignKeySpec> released, Func<TableDesignIndex, TableDesignIndex?> planned)
    {
        if (change.From is not { } from || inPlaceRename) yield break;
        var label = Label(index);
        if (change.To != null && Lost(from, rules) is { Count: > 0 } lost)
            yield return $"{label} cannot be re-created without losing its {string.Join(", ", lost)}.";
        if (from.IsUnique && from.Parts.All(p => p.Column != null))
            foreach (var reference in baseline.ReferencedBy.Where(r => SameSet(r.Columns, from.Parts.Select(p => p.Column!).ToList())
                                                                       && !TableKeyPlanning.Released(r, baseline, released)))
                yield return $"{label} is unique over the columns foreign key {TableDesignText.Name(reference.Name)} on " +
                             $"{TableDesignText.Qualified(reference.Table)} references; drop or change that foreign key first.";
        if (rules.ForeignKeysNeedIndex)
            foreach (var fk in baseline.ForeignKeys.Where(fk => OnlyIndexFor(fk, from, change.To, baseline, planned)
                                                                && !released.Any(r => r.Name != null && r.Name == fk.Name)))
                yield return $"{label} is the only index for foreign key {TableDesignText.Name(fk.Name)}, which {rules.Provider} " +
                             "needs; add an index on its columns first.";
    }

    /// <summary>Plans that drop an index and create one of the same name, on engines that cannot do both in one transaction.</summary>
    public static IEnumerable<string> NameReuseBlocked(List<(EditableIndex Index, TableIndexChange Change)> changes, TableIndexRules rules)
    {
        if (rules.ReusesNameInOneTransaction) yield break;
        var droppedNames = changes.Select(c => c.Change.From?.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, change) in changes.Where(c => c.Change.To is { } to && droppedNames.Contains(to.Name)))
            yield return $"{Label(index)} would be dropped and created under the same name in one save, which {rules.Provider} cannot do; " +
                         "give it a new name, or drop it in one save and add it in the next.";
    }

    /// <summary>
    /// The index leads with the foreign key's columns and no other index will once the plan has run (one the plan
    /// drops, or re-creates without that lead, does not count); a re-created one that still leads takes its place.
    /// </summary>
    private static bool OnlyIndexFor(TableDesignForeignKey fk, TableDesignIndex from, TableDesignIndex? to, TableDesign baseline,
        Func<TableDesignIndex, TableDesignIndex?> planned)
    {
        var columns = fk.Columns.Select(p => p.Column).ToList();
        bool Leads(TableDesignIndex? index) =>
            index != null && index.Parts.Select(p => p.Column).Take(columns.Count).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);
        return Leads(from) && !Leads(to) && !baseline.Indexes.Any(i => i.Name != from.Name && Leads(planned(i)));
    }

    /// <summary>What re-creating the index would not carry over from it.</summary>
    private static List<string> Lost(TableDesignIndex index, TableIndexRules rules)
    {
        var lost = new List<string>();
        if (index.Parts.Any(p => p.Expression != null) && !rules.Expressions) lost.Add("expression keys");
        if (index.Parts.Any(p => p.Collation != null || p.Options != null)) lost.Add("key column settings");
        if (index.IncludedColumns.Count > 0 && !rules.IncludedColumns) lost.Add("included columns");
        if (index.Filter != null && !rules.Filters) lost.Add("filter");
        if (!rules.KeepsMethod(index.Method)) lost.Add($"index method {index.Method}");
        if (index.Comment != null) lost.Add("comment");
        lost.AddRange(index.Properties);
        return lost;
    }

    /// <summary>Index changes Save may only run once confirmed: drops and re-creations, during which the index is missing.</summary>
    public static IEnumerable<string> Destructive(EditableIndex index, TableIndexChange change)
    {
        if (change.From is not { } from || DdlColumnRules.IsRename(change)) yield break;
        var parts = string.Join(", ", from.Parts.Select(TableDesignText.Describe));
        if (change.To == null)
            yield return from.IsUnique
                ? $"Drops unique index {from.Name}; values are no longer required to be unique in ({parts})."
                : $"Drops index {from.Name} on ({parts}); queries that used it may become slower.";
        else
            yield return $"Re-creates index {from.Name} as {Definition(change.To)} instead of {Definition(from)}; it is rebuilt from the table" +
                         (change.To.IsUnique ? ", and the save fails if existing rows violate it." : ".");
    }

    /// <summary>"[unique ](parts)[ include (...)][ where ...][ using ...]", for the review.</summary>
    private static string Definition(TableDesignIndex index) =>
        $"{(index.IsUnique ? "unique " : "")}({string.Join(", ", index.Parts.Select(TableDesignText.Describe))})" +
        (index.IncludedColumns.Count > 0 ? $" include ({string.Join(", ", index.IncludedColumns)})" : "") +
        (index.Filter != null ? $" where {index.Filter}" : "") + (index.Method != null ? $" using {index.Method}" : "");

    public static string Label(EditableIndex index) =>
        "Index " + (index.GivenName.Length > 0 ? index.GivenName : index.Loaded?.Name ?? $"({index.PartsDisplay})");

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
}
