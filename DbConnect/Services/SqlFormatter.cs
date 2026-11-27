using System;
using System.Collections.Generic;
using System.Text;

namespace DbConnect.Services;

/// <summary>Formats SQL using lexical tokens so quoted text and comments remain opaque.</summary>
public static class SqlFormatter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "ON",
        "AND", "OR", "GROUP", "ORDER", "BY", "HAVING", "LIMIT", "OFFSET", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "WITH", "AS", "CASE", "WHEN", "THEN", "ELSE", "END", "UNION",
        "ALL", "DISTINCT", "RETURNING", "EXISTS", "NULL", "IS", "NOT", "IN"
    };

    public static string Format(string sql, SqlBatchDialect dialect = SqlBatchDialect.Standard)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var tokens = SqlTokenizer.Tokenize(sql, dialect);
        var output = new StringBuilder();
        var parentheses = new Stack<bool>();
        var depth = 0;
        var caseDepth = 0;
        var inWith = false;
        var delimiter = ";";
        SqlToken? previous = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (dialect == SqlBatchDialect.MySql && delimiter != ";" &&
                token.Kind is not (SqlTokenKind.Quoted or SqlTokenKind.Comment or SqlTokenKind.Directive) &&
                sql.AsSpan(token.Start).StartsWith(delimiter, StringComparison.Ordinal))
            {
                Append(output, delimiter, depth + caseDepth, previous);
                var delimiterEnd = token.Start + delimiter.Length;
                while (i + 1 < tokens.Count && tokens[i + 1].Start < delimiterEnd) i++;
                NewLine(output);
                previous = token;
                continue;
            }
            var word = token.Kind == SqlTokenKind.Word ? token.Text.ToUpperInvariant() : "";
            var next = i + 1 < tokens.Count ? tokens[i + 1] : (SqlToken?)null;
            var pair = (word is "GROUP" or "ORDER" && Is(next, "BY"))
                || (word == "INSERT" && Is(next, "INTO"))
                || (word == "DELETE" && Is(next, "FROM"))
                || (word is "LEFT" or "RIGHT" or "FULL" or "INNER" or "CROSS" && Is(next, "JOIN"));

            if (token.Kind == SqlTokenKind.Directive)
            {
                if (dialect == SqlBatchDialect.MySql)
                    SqlBatchParser.TryReadDelimiterCommand(sql, token.Start, out delimiter, out _);
                NewLine(output);
                output.Append(token.Text.TrimEnd('\r', '\n'));
                NewLine(output);
            }
            else if (token.Kind == SqlTokenKind.Comment)
            {
                Append(output, token.Text, depth + caseDepth, previous);
                if (token.Text.StartsWith("--", StringComparison.Ordinal) || token.Text.Contains('\n')) NewLine(output);
            }
            else if (token.Text == "(")
            {
                var subquery = Is(next, "SELECT") || Is(next, "WITH");
                Append(output, "(", depth + caseDepth, previous);
                parentheses.Push(subquery);
                if (subquery) { depth++; NewLine(output); }
            }
            else if (token.Text == ")")
            {
                var subquery = parentheses.Count > 0 && parentheses.Pop();
                if (subquery) { depth = Math.Max(0, depth - 1); NewLine(output); }
                Append(output, ")", depth + caseDepth, previous);
            }
            else if (token.Text == ",")
            {
                Append(output, ",", depth + caseDepth, previous);
                if (inWith && parentheses.Count == 0) NewLine(output);
            }
            else if (token.Text == ";")
            {
                Append(output, ";", depth + caseDepth, previous);
                inWith = false;
                NewLine(output);
            }
            else
            {
                if (word == "WITH") inWith = true;
                if (word == "SELECT" && parentheses.Count == 0) inWith = false;
                if (word == "END") caseDepth = Math.Max(0, caseDepth - 1);
                if (IsClause(word, previous, parentheses.Count) || word is "WHEN" or "ELSE" or "END") NewLine(output);
                Append(output, Keywords.Contains(word) ? word : token.Text, depth + caseDepth, previous);
                if (word == "CASE") { caseDepth++; NewLine(output); }
                if (word is "THEN" or "ELSE") NewLine(output);
                if (pair)
                {
                    i++;
                    Append(output, next!.Value.Text.ToUpperInvariant(), depth + caseDepth, token);
                    previous = next;
                    continue;
                }
            }
            previous = token;
        }
        return output.ToString().TrimEnd();
    }

    private static bool Is(SqlToken? token, string word) => token is { Kind: SqlTokenKind.Word } &&
        token.Value.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsClause(string word, SqlToken? previous, int parenDepth) => word switch
    {
        "SELECT" or "FROM" or "WHERE" or "HAVING" or "LIMIT" or "OFFSET" or "UPDATE" or "SET"
            or "VALUES" or "RETURNING" or "UNION" or "INSERT" or "DELETE" or "GROUP" or "ORDER" => true,
        "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "ON" => true,
        "AND" or "OR" => parenDepth == 0 && previous is not null,
        _ => false
    };

    private static void Append(StringBuilder output, string text, int indent, SqlToken? previous)
    {
        if (output.Length == 0 || output[^1] == '\n') output.Append(' ', indent * 4);
        else if (NeedsSpace(output[^1], text, previous)) output.Append(' ');
        output.Append(text);
    }

    private static bool NeedsSpace(char last, string text, SqlToken? previous) =>
        last is not '(' and not '.' and not ':' && text is not ")" and not "," and not ";" and not "." and not "::" and not "->>" and not "->" and not "#>>" and not "#>" &&
        previous?.Text is not "->>" and not "->" and not "#>>" and not "#>" &&
        !(text.StartsWith("'", StringComparison.Ordinal) && previous is { Kind: SqlTokenKind.Word } && previous.Value.Text is "E" or "e" or "N" or "n") &&
        !(text == "(" && previous is { Kind: SqlTokenKind.Word } && !Keywords.Contains(previous.Value.Text));

    private static void NewLine(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] == ' ') output.Length--;
        if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
    }
}
