using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Check constraints in the designer's change plan: which are added, dropped, renamed or re-created,
/// which changes are blocked or need review, and which definitions are mistakes. Expressions are only
/// checked for obvious malformation with the tokenizer; the server judges whether they are valid.
/// </summary>
internal static class TableCheckPlanning
{
    public static IReadOnlyList<TableCheckSpec> CreateChecks(TableDesignerEdits e) =>
        e.Checks.Where(c => !c.IsDropped).Select(c => c.Spec()).ToList();

    /// <summary>
    /// The changes to the loaded checks and the added ones. An unchanged check that names a column being
    /// dropped is dropped first on engines that alter in place (SQL Server and MySQL refuse the column
    /// otherwise); a rebuild leaves it out as before.
    /// </summary>
    public static List<(EditableCheck Check, TableCheckChange Change)> Changes(TableDesignerEdits e, TableCheckRules rules)
    {
        var dropped = TableKeyPlanning.DroppedColumns(e);
        var changes = new List<(EditableCheck, TableCheckChange)>();
        foreach (var check in e.Checks)
        {
            if (check.IsNew)
            {
                if (!check.IsDropped) changes.Add((check, new(null, check.Spec())));
                continue;
            }
            var loaded = check.LoadedSpec()!;
            if (check.IsDropped || !e.RebuildsColumns && !check.ExpressionChanged && References(check.Loaded.Expression, dropped, e).Any())
                changes.Add((check, new(loaded, null)));
            else if (check.ExpressionChanged || check.Renamed && !rules.Renames)
                changes.Add((check, new(loaded, check.Spec())));
            else if (check.Renamed) changes.Add((check, new(loaded, loaded with { Name = check.Spec().Name })));
        }
        return changes;
    }

    /// <summary>Why a change cannot be made safely; empty when it can.</summary>
    public static IEnumerable<string> Blocked(EditableCheck check, TableCheckChange change, TableDesign baseline, bool inPlaceRename)
    {
        var label = Label(check);
        if (baseline.UnsupportedReason(TableDesignFeature.ConstraintChanges) is { } unsupported)
        {
            yield return $"{label} cannot be changed: changing checks after creation is {unsupported}.";
            yield break;
        }
        // A rename in place keeps everything; a re-creation (MySQL renames that way) or a rebuild restates only what the designer edits.
        if (change is { From: not null, To: not null } && !inPlaceRename && check.Loaded is { } loaded)
        {
            var lost = new[] { loaded.State, loaded.Comment != null ? "comment" : null }.OfType<string>().ToList();
            if (lost.Count > 0) yield return $"{label} cannot be re-created without losing its {string.Join(", ", lost)}.";
        }
    }

    /// <summary>Check changes Save may only run once confirmed.</summary>
    /// <remarks>A rename keeps the expression, so rows that met it still do, however the engine renames.</remarks>
    public static IEnumerable<string> Destructive(EditableCheck check, TableCheckChange change)
    {
        if (change.From is not { } from || DdlColumnRules.IsRename(change)) yield break;
        if (change.To == null) yield return $"Drops {Label(check)}; rows are no longer checked against ({from.Expression}).";
        else
            yield return $"Re-creates {Label(check)} as ({change.To.Expression}) instead of ({from.Expression}); existing rows are checked " +
                         "and the save fails if any violate it.";
    }

    /// <summary>Mistakes in the checks. Unchanged loaded checks are only checked for name clashes.</summary>
    public static IEnumerable<string> Problems(TableDesignerEdits e)
    {
        var active = e.Checks.Where(c => !c.IsDropped).ToList();
        foreach (var name in active.Select(c => c.Spec().Name).OfType<string>().GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Duplicate check constraint name: \"{name.Key}\".";
        var dropped = TableKeyPlanning.DroppedColumns(e);
        foreach (var check in active.Where(c => c.IsNew || c.ExpressionChanged))
        {
            var label = Label(check);
            if (string.IsNullOrWhiteSpace(check.Expression))
            {
                yield return $"{label} has no expression.";
                continue;
            }
            if (TableDesignerValidation.FragmentProblem(check.Expression, e.Capabilities) is { } problem)
                yield return $"{label}: the expression {problem}.";
            foreach (var column in References(check.Expression, dropped, e))
                yield return $"{label} refers to column \"{column}\", which is being dropped.";
        }
    }

    /// <summary>The loaded form of a dropped or re-created check names a column being dropped, so it must go before the column.</summary>
    public static bool UsesDroppedColumn(TableCheckChange change, TableDesignerEdits e) =>
        change.From is { } from && References(from.Expression, TableKeyPlanning.DroppedColumns(e), e).Any();

    /// <summary>The dropped columns an expression names, found among its words and quoted identifiers (not string literals).</summary>
    internal static IEnumerable<string> References(string expression, HashSet<string> dropped, TableDesignerEdits e) =>
        dropped.Count == 0 ? []
            : SqlTokenizer.Tokenize(expression, e.Capabilities.BatchDialect)
                .Where(t => t.Kind == SqlTokenKind.Word || t.Kind == SqlTokenKind.Quoted && t.Text.Length > 1 && t.Text[0] is '"' or '`' or '[')
                .Select(t => t.Kind == SqlTokenKind.Word ? t.Text : t.Text[1..^1])
                .Where(dropped.Contains).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string Label(EditableCheck check) =>
        "Check" + ((check.Spec().Name ?? check.Loaded?.Name) is { } name ? $" {name}" : $" ({(check.Loaded?.Expression ?? check.Expression).Trim()})");
}
