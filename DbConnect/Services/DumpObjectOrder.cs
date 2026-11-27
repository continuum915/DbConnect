using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

/// <summary>
/// Orders a dump's views and routines so each comes after the others its definition names, keeping the given order
/// otherwise. Engines record these dependencies differently, and some not at all, so they are read from the
/// definitions: an identifier outside comments and string literals that is another object's name is taken as a
/// reference to it. A false match only moves an object later; objects that name each other keep the given order.
/// </summary>
internal static class DumpObjectOrder
{
    public static IReadOnlyList<DumpObject> Sort(IReadOnlyList<DumpObject> objects)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < objects.Count; i++)
        {
            if (!byName.TryGetValue(objects[i].Identity.Name, out var list)) byName[objects[i].Identity.Name] = list = [];
            list.Add(i);
        }
        var needs = objects.Select((o, i) => Identifiers(o.Definition)
            .SelectMany(name => byName.TryGetValue(name, out var list) ? list : [])
            .Where(j => j != i).ToHashSet()).ToList();

        var placed = new bool[objects.Count];
        var result = new List<DumpObject>(objects.Count);
        while (result.Count < objects.Count)
        {
            var next = Enumerable.Range(0, objects.Count).FirstOrDefault(i => !placed[i] && needs[i].All(j => placed[j]), -1);
            // Objects that name each other: the earliest goes first, and the engine decides whether that loads.
            if (next < 0) next = Array.IndexOf(placed, false);
            placed[next] = true;
            result.Add(objects[next]);
        }
        return result;
    }

    /// <summary>The identifiers in SQL text, unquoted, skipping comments and string literals.</summary>
    internal static IEnumerable<string> Identifiers(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && At(sql, i + 1, '-')) i = Skip(sql, i, "\n");
            else if (c == '/' && At(sql, i + 1, '*')) i = Skip(sql, i + 2, "*/");
            else if (c == '\'') i = SkipQuoted(sql, i, '\'');
            else if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = SkipQuoted(sql, i, close);
                yield return sql[(i + 1)..Math.Max(i + 1, end - 1)].Replace(new string(close, 2), close.ToString());
                i = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                yield return sql[start..i];
            }
            else i++;
        }
    }

    private static bool At(string sql, int index, char c) => index < sql.Length && sql[index] == c;

    private static int Skip(string sql, int from, string terminator)
    {
        var end = sql.IndexOf(terminator, from, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + terminator.Length;
    }

    /// <summary>The index after a quoted token, where a doubled closing quote stands for itself.</summary>
    private static int SkipQuoted(string sql, int start, char close)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (At(sql, i + 1, close)) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }
}
