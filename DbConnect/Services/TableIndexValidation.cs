using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Mistakes in the designer's independent indexes: names, key parts, expressions, included columns, filters and
/// methods, and combinations the engine's rules rule out. Expressions and filters are only checked for obvious
/// malformation with the tokenizer; the server judges whether they are valid.
/// </summary>
internal static class TableIndexValidation
{
    /// <summary>What an index is checked against: the edits, the table's columns, and the columns being dropped or renamed.</summary>
    private sealed record Table(TableDesignerEdits Edits, HashSet<string> Columns, HashSet<string> Dropped, HashSet<string> RenamedFrom)
    {
        /// <summary>Columns a typed expression or filter names that it cannot: ones being dropped, or a renamed one's old name.</summary>
        public IEnumerable<(string Column, string Why)> Stale(string text) =>
            TableCheckPlanning.References(text, Dropped, Edits).Select(c => (c, "which is being dropped"))
                .Concat(TableCheckPlanning.References(text, RenamedFrom, Edits).Select(c => (c, "which is being renamed")));
    }

    /// <summary>Mistakes in the independent indexes. Unchanged loaded indexes are only checked for names.</summary>
    public static IEnumerable<string> Problems(TableDesignerEdits e, TableIndexRules rules)
    {
        var active = e.Indexes.Where(i => !i.IsDropped).ToList();
        var editable = active.Where(i => !i.IsReadOnly).ToList();
        foreach (var name in editable.Select(i => i.GivenName).Where(n => n.Length > 0)
                     .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Duplicate index name: \"{name.Key}\".";
        var columns = e.Columns.Where(c => !c.IsDropped).Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The old name of a renamed column, unless another column now has it. Index expressions and filters are text, which a rename does not follow.
        var renamedFrom = e.Columns.Where(c => !c.IsNew && !c.IsDropped && c.Original != null && !c.Original.Name.Equals(c.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Original!.Name).Where(n => !columns.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var table = new Table(e, columns, TableKeyPlanning.DroppedColumns(e), renamedFrom);
        foreach (var index in editable)
        {
            var label = TableIndexPlanning.Label(index);
            if (index.GivenName.Length == 0) yield return $"{label} has no name.";
            if (!index.IsNew && !TableIndexPlanning.Redefined(index, e)) continue;
            foreach (var problem in Parts(index, label, table, rules)) yield return problem;
            foreach (var problem in IncludedAndFilter(index, label, table)) yield return problem;
            foreach (var problem in Method(index, label, active, rules)) yield return problem;
            if (active.FirstOrDefault(other => other != index && other.SameDefinition(index)) is { } same)
                yield return $"{label} has the same definition as {TableIndexPlanning.Label(same)}.";
        }
    }

    private static IEnumerable<string> Parts(EditableIndex index, string label, Table table, TableIndexRules rules)
    {
        if (index.Parts.Count == 0) yield return $"{label} has no key columns.";
        var parts = index.KeyColumns.ToList();
        foreach (var column in parts.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"{label} lists column \"{column.Key}\" more than once.";
        foreach (var column in parts.Where(c => !table.Columns.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return table.Dropped.Contains(column) ? DroppedMember(label, column) : $"{label} refers to column \"{column}\", which the table does not have.";
        var expressions = index.Parts.Where(p => p.IsExpression).Select(p => p.Expression?.Trim() ?? "").ToList();
        if (!rules.Expressions && index.Parts.Any(p => p.IsExpression && p.Loaded == null))
            yield return $"{label} has an expression key; {rules.Provider} index keys are columns.";
        if (expressions.Any(x => x.Length == 0)) yield return $"{label} has an empty expression key.";
        foreach (var expression in expressions.Where(x => x.Length > 0).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"{label} lists expression ({expression.Key}) more than once.";
        foreach (var expression in expressions.Where(x => x.Length > 0).Distinct())
        {
            if (TableDesignerValidation.FragmentProblem(expression, table.Edits.Capabilities) is { } problem)
                yield return $"{label}: the key expression ({expression}) {problem}.";
            else if (OnlyAColumn(expression, table) is { } column)
                yield return $"{label} has the expression ({expression}), which is only column \"{column}\"; add the column as a key column instead.";
        }
        foreach (var (column, why) in expressions.SelectMany(table.Stale).Distinct())
            yield return $"{label} uses column \"{column}\", {why}, in a key expression; change the expression or drop the index.";
        if (!rules.Descending && index.Parts.Any(p => p.Descending))
            yield return $"{label} has a descending key; {rules.Provider} indexes have no descending keys.";
    }

    private static IEnumerable<string> IncludedAndFilter(EditableIndex index, string label, Table table)
    {
        var keys = index.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in index.IncludedColumns.GroupBy(c => c.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"{label} includes column \"{column.Key}\" more than once.";
        foreach (var column in index.IncludedColumns.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (keys.Contains(column)) yield return $"{label} includes column \"{column}\", which is already a key column.";
            else if (table.Dropped.Contains(column)) yield return DroppedMember(label, column);
            else if (!table.Columns.Contains(column)) yield return $"{label} includes column \"{column}\", which the table does not have.";
        }
        if (index.FilterText is not { } filter) yield break;
        if (TableDesignerValidation.FragmentProblem(filter, table.Edits.Capabilities) is { } problem) yield return $"{label}: the filter {problem}.";
        foreach (var (column, why) in table.Stale(filter))
            yield return $"{label} uses column \"{column}\", {why}, in its filter; change the filter or drop the index.";
    }

    /// <summary>What the index's method allows, where the designer offers that method.</summary>
    private static IEnumerable<string> Method(EditableIndex index, string label, List<EditableIndex> active, TableIndexRules rules)
    {
        if (index.Loaded == null && index.Method != null && rules.Method(index.Method) == null)
            yield return $"{label} uses method {index.Method}, which the designer does not offer for {rules.Provider}.";
        if (rules.Method(index.Method) is not { } method) yield break;
        var name = method.Name;
        if (index.IsUnique && !method.Unique) yield return $"{label} is unique, which {name} indexes cannot be.";
        if (index.Parts.Any(p => p.Descending) && !method.Descending) yield return $"{label} has a descending key, which {name} indexes cannot have.";
        if (index.IncludedColumns.Count > 0 && !method.Included) yield return $"{label} has included columns, which {name} indexes cannot have.";
        if (index.FilterText != null && !method.Filter) yield return $"{label} has a filter, which {name} indexes cannot have.";
        if (index.Parts.Count > 1 && !method.MultipleKeys) yield return $"{label} has more than one key part, which {name} indexes cannot have.";
        if (method.OnePerTable && active.FirstOrDefault(other => other != index && name.Equals(other.Method, StringComparison.OrdinalIgnoreCase)) is { } taken)
            yield return $"{label} is {name}, and {TableIndexPlanning.Label(taken)} already is; a table has one {name} index.";
    }

    private static string DroppedMember(string label, string column) =>
        $"{label} includes column \"{column}\", which is being dropped; remove it from the index or drop the index.";

    /// <summary>The column an expression consists of alone (MySQL refuses such a functional key part); null otherwise.</summary>
    private static string? OnlyAColumn(string expression, Table table)
    {
        var tokens = SqlTokenizer.Tokenize(expression, table.Edits.Capabilities.BatchDialect)
            .Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        if (tokens.Count != 1) return null;
        var token = tokens[0];
        var name = token.Kind == SqlTokenKind.Word ? token.Text
            : token.Kind == SqlTokenKind.Quoted && token.Text.Length > 1 && token.Text[0] is '"' or '`' or '[' ? token.Text[1..^1] : null;
        return name != null && table.Columns.Contains(name) ? name : null;
    }
}
