using DbConnect.Models;

namespace DbConnect.Tests;

/// <summary>
/// The drift guard between the two models of a table (issue 06): every part of the script
/// snapshot corresponds to a design element or an Unrepresented entry, and every design
/// element appears in the snapshot.
/// </summary>
internal static class TableDesignConsistency
{
    public static void AssertConsistent(TableDefinitionSnapshot snapshot, TableDesign design)
    {
        var problems = Problems(snapshot, design);
        Assert.True(problems.Count == 0, "Script and design disagree:\n" + string.Join("\n", problems));
    }

    public static List<string> Problems(TableDefinitionSnapshot snapshot, TableDesign design)
    {
        var problems = new List<string>();
        var named = StringComparer.OrdinalIgnoreCase;
        var gaps = design.Unrepresented.Select(g => g.Element).OfType<string>().ToHashSet(named);
        var constraints = new[] { design.PrimaryKey?.Name }.Concat(design.UniqueKeys.Select(k => k.Name))
            .Concat(design.ForeignKeys.Select(k => k.Name)).Concat(design.Checks.Select(c => c.Name))
            .OfType<string>().ToHashSet(named);
        var commented = new[] { design.PrimaryKey is { Comment: not null } pk ? pk.Name : null }
            .Concat(design.UniqueKeys.Where(k => k.Comment != null).Select(k => k.Name))
            .Concat(design.ForeignKeys.Where(k => k.Comment != null).Select(k => k.Name))
            .Concat(design.Checks.Where(c => c.Comment != null).Select(c => c.Name))
            .Concat(design.Indexes.Where(i => i.Comment != null).Select(i => i.Name))
            .Concat(design.Columns.Where(c => c.Comment != null).Select(c => c.Name))
            .Append(design.Comment != null ? design.Table.Name : null)
            .OfType<string>().ToHashSet(named);
        var indexes = design.Indexes.Select(i => i.Name).ToHashSet(named);
        var triggers = design.Triggers.Select(t => t.Name).ToHashSet(named);
        var withOptions = design.Columns.Where(c => c.Properties.Count > 0).Select(c => c.Name)
            .Concat(design.Indexes.Where(i => i.Properties.Count > 0).Select(i => i.Name))
            .Concat(design.Options.Count > 0 ? [design.Table.Name] : [])
            .ToHashSet(named);
        bool OwnedByColumn(string name) => design.Columns.Any(c => c.Sequence is { Owned: true } sequence && sequence.Name == name
                                                                   || c.Properties.Any(p => p.Contains(name, StringComparison.Ordinal)));

        foreach (var part in snapshot.Parts)
        {
            var matched = gaps.Contains(part.Name) || part.Kind switch
            {
                TableDefinitionPartKind.Table => named.Equals(part.Name, design.Table.Name),
                TableDefinitionPartKind.Sequence => OwnedByColumn(part.Name),
                TableDefinitionPartKind.Constraint => constraints.Contains(part.Name),
                TableDefinitionPartKind.Index => indexes.Contains(part.Name),
                TableDefinitionPartKind.Option => withOptions.Contains(part.Name) || OwnedByColumn(part.Name),
                TableDefinitionPartKind.Comment => commented.Contains(part.Name),
                TableDefinitionPartKind.Trigger => triggers.Contains(part.Name),
                _ => false
            };
            if (!matched) problems.Add($"Script part {part.Kind} {part.Name} has no design element.");
        }

        var script = snapshot.ToScript();
        bool InScript(string text) => script.Contains(text, StringComparison.OrdinalIgnoreCase)
                                      || script.Contains(text.Replace("'", "''"), StringComparison.OrdinalIgnoreCase);
        foreach (var column in design.Columns)
        {
            if (!InScript(column.Name)) problems.Add($"Column {column.Name} is not in the script.");
            if (column.Comment != null && !InScript(column.Comment)) problems.Add($"Comment of column {column.Name} is not in the script.");
            foreach (var property in column.Properties.Where(p => !InScript(p) && !InScript(p.Split(' ')[^1])))
                problems.Add($"Property {property} of column {column.Name} is not in the script.");
        }
        foreach (var name in constraints.Where(n => !InScript(n)))
            problems.Add($"Constraint {name} is not in the script.");
        foreach (var index in design.Indexes)
        {
            var present = index.Origin switch
            {
                // MySQL declares indexes inside its CREATE TABLE text rather than as separate parts.
                TableDesignIndexOrigin.CreateIndex => snapshot.Parts.Any(p => p.Kind == TableDefinitionPartKind.Index && named.Equals(p.Name, index.Name))
                                                      || snapshot.Parts.Any(p => p.Kind == TableDefinitionPartKind.Table && p.Sql.Contains(index.Name, StringComparison.Ordinal)),
                TableDesignIndexOrigin.PrimaryKey => design.PrimaryKey != null,
                _ => design.UniqueKeys.Any(k => named.Equals(k.Name, index.Name) || k.Columns.SequenceEqual(index.Parts.Select(p => p.Column)))
            };
            if (!present) problems.Add($"Index {index.Name} ({index.Origin}) is not in the script.");
            if (index.Comment != null && !InScript(index.Comment)) problems.Add($"Comment of index {index.Name} is not in the script.");
        }
        foreach (var trigger in design.Triggers.Where(t => !snapshot.Parts.Any(p => p.Kind == TableDefinitionPartKind.Trigger && named.Equals(p.Name, t.Name))))
            problems.Add($"Trigger {trigger.Name} is not in the script.");
        foreach (var option in design.Options.Where(o => !InScript(o.Value) && !InScript(o.Name)))
            problems.Add($"Option {option.Name}={option.Value} is not in the script.");
        if (design.Comment != null && !InScript(design.Comment)) problems.Add("The table comment is not in the script.");
        return problems;
    }

    /// <summary>Compares two designs element by element, listing the first differences.</summary>
    public static void AssertSame(IReadOnlyList<string> expected, TableDesign actual)
    {
        var lines = TableDesignText.Lines(actual);
        Assert.True(expected.SequenceEqual(lines),
            "Design differs.\nExpected:\n  " + string.Join("\n  ", expected) + "\nActual:\n  " + string.Join("\n  ", lines));
    }
}
