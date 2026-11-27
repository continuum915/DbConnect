using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Models;

/// <summary>
/// A canonical, line-per-element rendering of a <see cref="TableDesign"/>. Two designs are the
/// same exactly when their lines are equal, which is how loads are compared after a save and in tests.
/// Trigger bodies are left out; they are compared through <see cref="TableDesignTrigger.Definition"/>.
/// </summary>
public static class TableDesignText
{
    public static IReadOnlyList<string> Lines(TableDesign design)
    {
        var lines = new List<string>
        {
            $"table {Qualified(design.Table)}"
        };
        lines.AddRange(design.Columns.Select(Column));
        if (design.PrimaryKey is { } pk) lines.Add($"primary key {Name(pk.Name)} ({string.Join(", ", pk.Columns)}){Suffix("comment", pk.Comment)}");
        lines.AddRange(design.UniqueKeys.Select(k => $"unique {Name(k.Name)} ({string.Join(", ", k.Columns)}){Suffix("comment", k.Comment)}"));
        lines.AddRange(design.ForeignKeys.Select(ForeignKey));
        lines.AddRange(design.ReferencedBy.Select(r => $"referenced by {Name(r.Name)} from {Qualified(r.Table)} ({string.Join(", ", r.Columns)})"));
        lines.AddRange(design.Checks.Select(c =>
            $"check {Name(c.Name)}{Suffix("column", c.Column)} {c.Expression}{Suffix("state", c.State)}{Suffix("comment", c.Comment)}"));
        lines.AddRange(design.Indexes.Select(Index));
        lines.AddRange(design.Triggers.Select(t => $"trigger {t.Name} {t.Timing} {t.Event}{Suffix("state", t.State)}"));
        lines.AddRange(design.Options.Select(o => $"option {o.Name}={o.Value}"));
        if (design.Comment != null) lines.Add($"comment {design.Comment}");
        lines.AddRange(design.Unsupported.OrderBy(u => u.Key).Select(u => $"unsupported {u.Key}: {u.Value}"));
        lines.AddRange(design.Unrepresented.Select(g => $"unrepresented {g.Description}"));
        return lines;
    }

    public static string Name(string? name) => name ?? "(unnamed)";

    public static string Describe(TableDesignIndexPart part) =>
        (part.Column ?? $"({part.Expression})") + (part.Descending ? " DESC" : "") +
        (part.Collation != null ? $" COLLATE {part.Collation}" : "") + (part.Options != null ? " " + part.Options : "");

    public static string Describe(TableDesignIdentity identity) =>
        identity.Kind + (identity.Seed != null || identity.Increment != null ? $"({identity.Seed}, {identity.Increment})" : "") +
        (identity.Options != null ? " " + identity.Options : "");

    public static string Describe(TableDesignGenerated generated) =>
        $"AS ({generated.Expression}) {generated.Storage.ToString().ToUpperInvariant()}";

    public static string Describe(TableDesignSequence sequence) =>
        (sequence.Owned ? "owns sequence " : "uses sequence ") + sequence.Name;

    public static string Pairs(TableDesignForeignKey key) =>
        string.Join(", ", key.Columns.Select(p => $"{p.Column} -> {p.ReferencedColumn}"));

    public static string Qualified(DatabaseObjectIdentity table) =>
        string.Join(".", new[] { table.Catalog, table.Schema, table.Name }.Where(s => s != null));

    private static string Column(TableDesignColumn c) =>
        $"column {c.Name} {c.DataType}{(c.IsNullable ? "" : " NOT NULL")}{Suffix("default", c.Default)}{Suffix("collate", c.Collation)}" +
        Suffix("generated", c.Generated is { } g ? Describe(g) : null) +
        Suffix("identity", c.Identity is { } i ? Describe(i) : null) +
        Suffix("sequence", c.Sequence is { } s ? Describe(s) : null) +
        Suffix("comment", c.Comment) + (c.Properties.Count > 0 ? $" [{string.Join("; ", c.Properties)}]" : "");

    private static string ForeignKey(TableDesignForeignKey k) =>
        $"foreign key {Name(k.Name)} ({Pairs(k)}) references {Qualified(k.ReferencedTable)} on delete {k.OnDelete} on update {k.OnUpdate}" +
        Suffix("state", k.State) + Suffix("comment", k.Comment);

    private static string Index(TableDesignIndex i) =>
        $"index {i.Name}{(i.IsUnique ? " unique" : "")} {i.Origin} ({string.Join(", ", i.Parts.Select(Describe))})" +
        (i.IncludedColumns.Count > 0 ? $" include ({string.Join(", ", i.IncludedColumns)})" : "") +
        Suffix("where", i.Filter) + Suffix("using", i.Method) +
        (i.Properties.Count > 0 ? $" [{string.Join("; ", i.Properties)}]" : "") + Suffix("comment", i.Comment);

    private static string Suffix(string label, string? value) => value == null ? "" : $" {label}={value}";
}
