using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Recognises a dump's tables: in the names <see cref="ForeignKeyService"/> writes, "schema.table" (MySQL
/// "database.table", SQLite just "table"), which may be quoted, and by the raw names of a snapshot prerequisite's
/// object. A qualified name must match exactly; a bare name matches when only one table has it, ignoring case if
/// nothing matches exactly.
/// </summary>
internal sealed class DumpTableNames(IReadOnlyList<DatabaseObjectIdentity> tables)
{
    public int? Find(string spelled)
    {
        // Unquoted metadata may contain dots inside names, so try the text whole before splitting it.
        if (Single(i => Paths(tables[i]).Any(p => p.Length > 1 && string.Join(".", p) == spelled)) is { } whole) return whole;
        var parts = Parse(spelled);
        if (parts.Count > 1) return Single(i => Paths(tables[i]).Any(p => p.SequenceEqual(parts, StringComparer.Ordinal)));
        var name = parts.Count == 1 ? parts[0] : spelled;
        return Single(i => tables[i].Name.Equals(name, StringComparison.Ordinal))
               ?? Single(i => tables[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The dump's table with these raw names. A part either side leaves out is not compared; the name matches exactly,
    /// or ignoring case when that matches one table only.
    /// </summary>
    public int? Find(DatabaseObjectIdentity table)
    {
        static bool Part(string? left, string? right) => left == null || right == null || left == right;
        bool Matches(int i, StringComparison comparison) => tables[i].Name.Equals(table.Name, comparison) &&
                                                            Part(tables[i].Schema, table.Schema) && Part(tables[i].Catalog, table.Catalog);
        return Single(i => Matches(i, StringComparison.Ordinal)) ?? Single(i => Matches(i, StringComparison.OrdinalIgnoreCase));
    }

    private int? Single(Func<int, bool> matches)
    {
        var found = Enumerable.Range(0, tables.Count).Where(matches).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    private static IEnumerable<string[]> Paths(DatabaseObjectIdentity table)
    {
        if (!string.IsNullOrEmpty(table.Schema)) yield return [table.Schema, table.Name];
        if (!string.IsNullOrEmpty(table.Catalog)) yield return [table.Catalog, table.Name];
        if (!string.IsNullOrEmpty(table.Catalog) && !string.IsNullOrEmpty(table.Schema)) yield return [table.Catalog, table.Schema, table.Name];
    }

    /// <summary>Splits a dotted name into its parts, unquoting "…", `…` and […] with their doubled closing characters.</summary>
    private static List<string> Parse(string text)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var close = text[i] switch { '"' => '"', '`' => '`', '[' => ']', _ => '\0' };
            if (close != '\0' && part.Length == 0)
            {
                for (i++; i < text.Length; i++)
                {
                    if (text[i] != close) part.Append(text[i]);
                    else if (i + 1 < text.Length && text[i + 1] == close) part.Append(text[++i]);
                    else break;
                }
            }
            else if (text[i] == '.')
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            else part.Append(text[i]);
        }
        parts.Add(part.ToString());
        return parts;
    }
}
