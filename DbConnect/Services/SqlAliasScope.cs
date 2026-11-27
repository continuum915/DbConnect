using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

/// <summary>Resolves relation aliases within the query block containing the caret.</summary>
internal static class SqlAliasScope
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "AND", "OR", "SET", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "JOIN", "SELECT", "FROM", "AS", "INTO",
        "UPDATE", "UNION", "EXCEPT", "INTERSECT", "RETURNING", "WINDOW", "FETCH", "FOR"
    };

    public static Dictionary<string, string> AliasesAt(string sql, int caret, bool preserveQuotes = false)
    {
        var tokens = SqlTokenizer.Tokenize(sql, SqlBatchDialect.PostgreSql)
            .Where(t => t.Kind != SqlTokenKind.Comment).ToArray();
        var owners = new int[tokens.Length];
        var stack = new Stack<int>();
        var scope = 0;
        var nextScope = 0;
        var caretScope = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Start >= caret && (i == 0 || tokens[i - 1].Start < caret))
                caretScope = scope;
            owners[i] = scope;
            if (tokens[i].Text == "(")
            {
                stack.Push(scope);
                if (i + 1 < tokens.Length && (Is(tokens[i + 1], "SELECT") || Is(tokens[i + 1], "WITH")))
                    scope = ++nextScope;
            }
            else if (tokens[i].Text == ")" && stack.Count > 0)
                scope = stack.Pop();
            else if (tokens[i].Text == ";" || Is(tokens[i], "UNION") || Is(tokens[i], "EXCEPT") || Is(tokens[i], "INTERSECT"))
                scope = ++nextScope;
        }
        if (tokens.Length == 0 || caret > tokens[^1].Start)
            caretScope = scope;

        var aliases = new Dictionary<string, string>(preserveQuotes ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (owners[i] != caretScope || !(Is(tokens[i], "FROM") || Is(tokens[i], "JOIN") || Is(tokens[i], "UPDATE") || Is(tokens[i], "INTO")))
                continue;
            var j = i + 1;
            if (j >= tokens.Length || !Identifier(tokens[j]))
                continue;
            var table = preserveQuotes ? tokens[j++].Text : Name(tokens[j++]);
            while (j + 1 < tokens.Length && tokens[j].Text == "." && Identifier(tokens[j + 1]))
            {
                table += "." + (preserveQuotes ? tokens[j + 1].Text : Name(tokens[j + 1]));
                j += 2;
            }
            if (j < tokens.Length && Is(tokens[j], "AS"))
                j++;
            if (j < tokens.Length && owners[j] == caretScope && Identifier(tokens[j]) && !Reserved.Contains(tokens[j].Text))
                aliases[preserveQuotes ? tokens[j].Text : Name(tokens[j])] = table;
        }
        return aliases;
    }

    private static bool Is(SqlToken token, string word) =>
        token.Kind == SqlTokenKind.Word && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool Identifier(SqlToken token) => token.Kind == SqlTokenKind.Word ||
        token.Kind == SqlTokenKind.Quoted && token.Text[0] is '"' or '`' or '[';

    private static string Name(SqlToken token) => SqlCompletionNames.Decode(token.Text);
}
