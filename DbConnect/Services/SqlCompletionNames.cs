using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

internal static class SqlCompletionNames
{
    public static string Decode(string token) => token.Length > 1 ? token[0] switch
    {
        '"' => token[1..^1].Replace("\"\"", "\""),
        '`' => token[1..^1].Replace("``", "`"),
        '[' => token[1..^1].Replace("]]", "]"),
        _ => token
    } : token;

    public static string[] Parse(string name) => SqlTokenizer.Tokenize(name, SqlBatchDialect.PostgreSql)
        .Where(t => t.Text != ".").Select(t => t.Text).ToArray();

    public static string[]? Qualifier(string text, int caret)
    {
        var end = SqlCompletionEngine.WordStart(text, caret);
        var tokens = SqlTokenizer.Tokenize(text[..end], SqlBatchDialect.PostgreSql);
        if (tokens.Count == 0 || tokens[^1].Text != ".") return null;
        var parts = new List<string>();
        for (var i = tokens.Count - 2; i >= 0; i -= 2)
        {
            var token = tokens[i];
            if (token.Kind != SqlTokenKind.Word && !(token.Kind == SqlTokenKind.Quoted && token.Text[0] is '"' or '`' or '[')) break;
            parts.Insert(0, token.Text);
            if (i == 0 || tokens[i - 1].Text != ".") break;
        }
        return parts.ToArray();
    }
}
