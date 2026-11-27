using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services.Builders;

/// <summary>Removes constraints whose local columns no longer exist in a rebuilt table.</summary>
internal static class SqliteConstraintDependencies
{
    public static bool ReferencesAny(string sql, IReadOnlySet<string> names)
    {
        var tokens = SqliteDefinitionSql.Tokens(sql);
        var castTypeDepth = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Is("AS")) { castTypeDepth = token.Depth; continue; }
            if (castTypeDepth >= 0)
            {
                if (token.Depth >= castTypeDepth) continue;
                castTypeDepth = -1;
            }
            if (i > 0 && tokens[i - 1].Is("COLLATE")) continue;
            if (tokens.ElementAtOrDefault(i + 1)?.Is(".") == true) continue;
            if ((!token.Quoted || token.Text[0] != '\'') && names.Contains(token.Identifier)
                && (token.Quoted || tokens.ElementAtOrDefault(i + 1)?.Is("(") != true)) return true;
        }
        return false;
    }

    public static bool KeepTableConstraint(string term, string tableName, IReadOnlySet<string> dropped,
        ICollection<string>? removed)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var open = tokens.FindIndex(t => t.Is("("));
        var close = tokens.FindIndex(open + 1, t => t.Is(")") && t.Depth == tokens[open].Depth);
        if (!ReferencesAny(term[tokens[open].End..tokens[close].Start], dropped)
            && !ReferencesDroppedParent(term, tableName, dropped)) return true;
        removed?.Add(Describe(term));
        return false;
    }

    public static string FilterColumn(string term, string tableName, IReadOnlySet<string> dropped,
        bool columnDropped, ICollection<string>? removed)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var edits = new List<(int Start, int End)>();
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Depth != 0 || !(token.Is("CHECK") || token.Is("REFERENCES")
                || token.Is("UNIQUE") || token.Is("PRIMARY"))) continue;
            var start = i >= 2 && tokens[i - 2].Is("CONSTRAINT") ? tokens[i - 2].Start : token.Start;
            var endIndex = i + 1;
            if (token.Is("CHECK"))
            {
                endIndex = tokens.FindIndex(i + 1, t => t.Is(")") && t.Depth == 0) + 1;
            }
            else
            {
                while (endIndex < tokens.Count && !IsNextConstraint(tokens, endIndex)) endIndex++;
            }
            var end = endIndex < tokens.Count ? tokens[endIndex].Start : term.Length;
            var part = term[start..end];
            var affected = columnDropped || (token.Is("CHECK") && ReferencesAny(term[token.End..end], dropped))
                || (token.Is("REFERENCES") && ReferencesDroppedParent(part, tableName, dropped));
            if (affected)
            {
                removed?.Add(Describe(part, tokens[0].Identifier));
                edits.Add((start, end));
            }
            i = endIndex - 1;
        }
        foreach (var (start, end) in edits.OrderByDescending(e => e.Start))
            term = term.Remove(start, end - start);
        return term;
    }

    private static bool IsNextConstraint(List<SqliteDefinitionSql.Token> tokens, int index)
    {
        var token = tokens[index];
        if (token.Depth != 0) return false;
        if (tokens[index - 1].Is("SET") && (token.Is("NULL") || token.Is("DEFAULT"))) return false;
        return new[] { "CONSTRAINT", "CHECK", "REFERENCES", "UNIQUE", "PRIMARY", "NOT", "DEFAULT", "COLLATE", "GENERATED" }.Any(token.Is);
    }

    private static bool ReferencesDroppedParent(string term, string tableName, IReadOnlySet<string> dropped)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var references = tokens.FindIndex(t => t.Is("REFERENCES"));
        if (references < 0 || !tokens[references + 1].Identifier.Equals(tableName, StringComparison.OrdinalIgnoreCase)) return false;
        var open = tokens.FindIndex(references + 2, t => t.Is("("));
        if (open < 0) return false;
        var close = tokens.FindIndex(open + 1, t => t.Is(")") && t.Depth == tokens[open].Depth);
        return ReferencesAny(term[tokens[open].End..tokens[close].Start], dropped);
    }

    private static string Describe(string term, string? column = null)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var named = tokens[0].Is("CONSTRAINT");
        var kind = tokens[named ? 2 : 0].Identifier;
        var name = named ? tokens[1].Identifier : column ?? term.Trim();
        return $"{kind} {name}";
    }
}
