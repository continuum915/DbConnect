using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Primary and unique keys in the designer's change plan: which keys are added, dropped, renamed or
/// re-created, which of those changes are blocked (by the engine, by foreign keys that depend on a
/// key, or by backing-index properties a re-created key would lose), which need review, and which
/// key definitions are mistakes.
/// </summary>
internal static class TableKeyPlanning
{
    /// <summary>The keys CREATE TABLE declares: the edited keys, or the columns' flags when there are none.</summary>
    public static IReadOnlyList<TableKeySpec> CreateKeys(TableDesignerEdits e, IEnumerable<ColumnDefinition> columns) =>
        e.Keys.Count == 0 ? DdlColumnRules.KeysFromColumns(columns) : e.Keys.Where(k => !k.IsDropped).Select(k => k.Spec()).ToList();

    /// <summary>
    /// The changes to the loaded keys and the added keys, in the order the keys are listed. An unchanged
    /// key over a column being dropped is dropped first on engines that alter in place, so SQL Server does
    /// not refuse the column and MySQL does not silently shrink the key; a rebuild leaves it out as before.
    /// </summary>
    public static List<(KeyDefinition Key, TableKeyChange Change)> Changes(TableDesignerEdits e, TableKeyRules rules)
    {
        var dropped = DroppedColumns(e);
        var changes = new List<(KeyDefinition, TableKeyChange)>();
        foreach (var key in e.Keys)
        {
            if (key.IsNew)
            {
                if (!key.IsDropped) changes.Add((key, new(null, key.Spec())));
                continue;
            }
            var loaded = Loaded(key);
            var membersChanged = MembersChanged(key, e);
            if (key.IsDropped || !e.RebuildsColumns && !membersChanged && key.Columns.Any(c => dropped.Contains(c.Trim())))
                changes.Add((key, new(loaded, null)));
            else if (membersChanged || key.Renamed && !rules.Renames)
                changes.Add((key, new(loaded, key.Spec() with { Method = rules.RestatesMethod ? key.BackingIndex?.Method : null })));
            else if (key.Renamed) changes.Add((key, new(loaded, loaded with { Name = key.Spec().Name })));
        }
        return changes;
    }

    /// <summary>
    /// The key's members differ from the loaded ones. A member is compared by the loaded name of its column,
    /// so renaming a column (which the engine carries into its keys) does not re-create the key.
    /// </summary>
    public static bool MembersChanged(KeyDefinition key, TableDesignerEdits e) =>
        key.Loaded is { } loaded && !loaded.Columns.SequenceEqual(key.Columns.Select(member =>
            e.Columns.FirstOrDefault(c => !c.IsNew && c.Original != null && c.Name.Equals(member, StringComparison.OrdinalIgnoreCase))?.Original!.Name
            ?? member));

    /// <summary>The loaded key as the builders address it.</summary>
    private static TableKeySpec Loaded(KeyDefinition key) =>
        new(key.Kind, key.Loaded!.Name, key.Loaded.Columns) { IndexName = key.BackingIndex?.Name, Method = key.BackingIndex?.Method };

    /// <summary>Why a change cannot be made safely; empty when it can.</summary>
    public static IEnumerable<string> Blocked(KeyDefinition key, TableKeyChange change, TableDesign baseline, TableKeyRules rules,
        IReadOnlyList<TableForeignKeySpec> released)
    {
        var label = Label(key);
        if (baseline.UnsupportedReason(TableDesignFeature.ConstraintChanges) is { } unsupported)
        {
            yield return $"{label} cannot be changed: changing keys after creation is {unsupported}.";
            yield break;
        }
        if (change.From is not { } from || DdlColumnRules.IsRename(change)) yield break;
        foreach (var reference in baseline.ReferencedBy.Where(r => SameSet(r.Columns, from.Columns) && !Released(r, baseline, released)))
            yield return $"{label} is referenced by foreign key {TableDesignText.Name(reference.Name)} on " +
                         $"{TableDesignText.Qualified(reference.Table)}; drop or change that foreign key first.";
        if (rules.ForeignKeysNeedIndex)
            foreach (var fk in baseline.ForeignKeys.Where(fk => OnlyIndexFor(fk, from, change.To, key, baseline)
                                                                && !released.Any(r => r.Name != null && r.Name == fk.Name)))
                yield return $"{label} is the only index for foreign key {TableDesignText.Name(fk.Name)}, which {rules.Provider} " +
                             "needs; add an index on its columns first.";
        if (change.To != null && Lost(key, rules) is { Count: > 0 } lost)
            yield return $"{label} cannot be re-created with new columns without losing its {string.Join(", ", lost)}.";
    }

    /// <summary>A self-reference this plan drops or re-creates no longer depends on the key.</summary>
    internal static bool Released(TableDesignReference reference, TableDesign baseline, IReadOnlyList<TableForeignKeySpec> released) =>
        TableDesignText.Qualified(reference.Table) == TableDesignText.Qualified(baseline.Table)
        && released.Any(r => reference.Name != null ? r.Name == reference.Name : SameSet(r.ReferencedColumns.ToList(), reference.Columns));

    /// <summary>
    /// The key leads with the foreign key's columns and no other index does, so the engine cannot drop it.
    /// A re-created key that still leads with them takes its place.
    /// </summary>
    private static bool OnlyIndexFor(TableDesignForeignKey fk, TableKeySpec from, TableKeySpec? to, KeyDefinition key, TableDesign baseline)
    {
        var columns = fk.Columns.Select(p => p.Column).ToList();
        bool Leads(IEnumerable<string?> parts) => parts.Take(columns.Count).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);
        return Leads(from.Columns) && !(to != null && Leads(to.Columns))
               && !baseline.Indexes.Any(i => i != key.BackingIndex && Leads(i.Parts.Select(p => p.Column)));
    }

    /// <summary>What re-creating the key would not carry over from it and its backing index.</summary>
    private static List<string> Lost(KeyDefinition key, TableKeyRules rules)
    {
        var lost = new List<string>();
        if (key.Loaded?.Comment != null) lost.Add("comment");
        if (key.BackingIndex is not { } index) return lost;
        if (index.Method != null && !rules.KeptMethods.Contains(index.Method)) lost.Add($"index method {index.Method}");
        if (index.Parts.Any(p => p.Descending)) lost.Add("descending key columns");
        if (index.Parts.Any(p => p.Collation != null || p.Options != null)) lost.Add("key column settings");
        if (index.IncludedColumns.Count > 0) lost.Add("included columns");
        if (index.Filter != null) lost.Add("filter");
        if (index.Comment != null) lost.Add("index comment");
        lost.AddRange(index.Properties);
        return lost;
    }

    /// <summary>Key changes Save may only run once confirmed.</summary>
    public static IEnumerable<string> Destructive(KeyDefinition key, TableKeyChange change)
    {
        if (change.From is not { } from || DdlColumnRules.IsRename(change)) yield break;
        var what = key.Kind == KeyKind.Primary ? "rows are no longer identified by" : "values are no longer required to be unique in";
        if (change.To == null)
            yield return $"Drops {Label(key)}; {what} ({string.Join(", ", from.Columns)}).";
        else
            yield return $"Re-creates {Label(key)} on ({string.Join(", ", change.To.Columns)}) instead of ({string.Join(", ", from.Columns)}); " +
                         "if existing rows violate it the save fails.";
    }

    /// <summary>Mistakes in the key definitions.</summary>
    public static IEnumerable<string> Problems(TableDesignerEdits e, TableKeyRules rules)
    {
        var keys = e.Keys.Where(k => !k.IsDropped).ToList();
        // Duplicate column names are reported by the column checks; the first of them stands for the name here.
        var columns = e.Columns.Where(c => !c.IsDropped).GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dropped = DroppedColumns(e);
        if (keys.Count(k => k.Kind == KeyKind.Primary) > 1) yield return "A table can have only one primary key.";
        foreach (var key in keys)
        {
            var label = Label(key);
            if (key.Columns.Count == 0) yield return $"{label} has no columns.";
            foreach (var member in key.Columns.GroupBy(c => c.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return $"{label} lists column \"{member.Key}\" more than once.";
            // An unchanged loaded key goes with a dropped column (see Changes); an edited one must leave it out.
            foreach (var member in key.Columns.Where(c => !columns.ContainsKey(c.Trim())
                                                          && !(dropped.Contains(c.Trim()) && !key.IsNew && !MembersChanged(key, e))))
                yield return dropped.Contains(member.Trim())
                    ? $"{label} includes column \"{member}\", which is being dropped; remove it from the key or drop the key."
                    : $"{label} refers to column \"{member}\", which the table does not have.";
            if (key.Kind == KeyKind.Primary && !rules.PrimaryKeyNamed && !string.IsNullOrWhiteSpace(key.Name))
                yield return $"{rules.Provider} always names the primary key PRIMARY; leave its name blank.";
            if (key.Kind == KeyKind.Primary && rules.PrimaryKeyNeedsNotNull && (key.IsNew || MembersChanged(key, e)))
                foreach (var member in key.Columns.Where(c => columns.TryGetValue(c.Trim(), out var column) && column.IsNullable))
                    yield return $"Column \"{member}\" is in the primary key, so it must be Not Null.";
        }
        foreach (var group in keys.Where(k => k.Columns.Count > 0)
                     .GroupBy(k => string.Join("\u0001", k.Columns.Select(c => c.Trim().ToLowerInvariant()))).Where(g => g.Count() > 1))
            yield return $"{Label(group.ElementAt(1))} has the same columns as {Label(group.First())}.";

        // Keys, checks and foreign keys share the table's constraint names; a key also names the index it creates, so
        // keys and indexes share names too. Foreign keys and checks may share a name with an index (MySQL names the
        // index it adds for a foreign key after it). Two foreign keys, two checks or two indexes are reported by their
        // own checks; each key counts as its own kind, since keys have no such check.
        var constraints = keys.Select((k, i) => (Name: k.Spec().Name, Kind: $"key {i}"))
            .Concat(e.Checks.Where(c => !c.IsDropped).Select(c => (Name: c.Spec().Name, Kind: "check")))
            .Concat(e.ForeignKeys.Where(f => !f.IsDropped).Select(f => (Name: f.Spec()?.Name, Kind: "foreign key")));
        var keyAndIndexNames = keys.Select((k, i) => (Name: k.Spec().Name, Kind: $"key {i}"))
            .Concat(e.Indexes.Where(i => !i.IsDropped && !i.IsReadOnly).Select(i => (Name: (string?)i.GivenName, Kind: "index")));
        foreach (var name in new[] { constraints, keyAndIndexNames }.SelectMany(Clashes).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return $"The name \"{name}\" is used by more than one key, constraint or index.";
    }

    private static IEnumerable<string> Clashes(IEnumerable<(string? Name, string Kind)> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n.Name)).Select(n => (Name: n.Name!.Trim(), n.Kind))
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(n => n.Kind).Distinct().Count() > 1).Select(g => g.Key);
    internal static HashSet<string> DroppedColumns(TableDesignerEdits e) =>
        e.Columns.Where(c => c.IsDropped && !c.IsNew).Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Label(KeyDefinition key) =>
        (key.Kind == KeyKind.Primary ? "Primary key" : "Unique key") +
        ((key.Spec().Name ?? key.Loaded?.Name) is { } name ? $" {name}" : $" ({string.Join(", ", key.Loaded?.Columns ?? key.Columns)})");

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
}
