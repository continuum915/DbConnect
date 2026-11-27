using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DbConnect.Models;

namespace DbConnect.Services;

public static class SqlParameterDetector
{
    public static List<DetectedParameter> Detect(string sql, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ignored = GetIgnoredRanges(sql, cancellationToken);
        var firstLocalEnd = SqlParameterTokens.FirstLocalVariableEnd(sql, cancellationToken);
        var parameters = new Dictionary<string, DetectedParameter>(StringComparer.Ordinal);
        var lookup = parameters.GetAlternateLookup<ReadOnlySpan<char>>();
        var ignoredIndex = 0;
        foreach (var token in SqlParameterTokens.Scan(sql, cancellationToken))
        {
            while (ignoredIndex < ignored.Count && ignored[ignoredIndex].End < token.Start) ignoredIndex++;
            if (ignoredIndex < ignored.Count && ignored[ignoredIndex].Start <= token.Start) continue;
            if (token.Style == ParameterPlaceholderStyle.NamedAt && token.Start == firstLocalEnd) continue;
            var placeholder = sql.AsSpan(token.Start, token.Length);
            if (lookup.ContainsKey(placeholder)) continue;
            int? ordinal = token.Style is ParameterPlaceholderStyle.PositionalDollar or ParameterPlaceholderStyle.PositionalQuestion
                && token.Length > 1 && int.TryParse(placeholder[1..], out var index) ? index : null;
            var name = placeholder.ToString();
            var hint = token.Style switch
            {
                ParameterPlaceholderStyle.NamedAt => "SQL Server/SQLite",
                ParameterPlaceholderStyle.NamedColon => "SQLite/Oracle",
                ParameterPlaceholderStyle.PositionalDollar => "PostgreSQL",
                _ => "MySQL/SQLite"
            };
            parameters.Add(name, new(name, ordinal, token.Style, hint));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return parameters.Values.OrderBy(p => p.Style).ThenBy(p => p.OrdinalIndex ?? 0)
            .ThenBy(p => p.Placeholder).ToList();
    }

    public static string Substitute(string sql, List<QueryParamEntry> parameters, CancellationToken cancellationToken = default)
        => SubstituteWithOffsets(sql, parameters, cancellationToken).Sql;

    /// <summary>Substitutes values and maps each resulting character to its source SQL offset.</summary>
    public static SqlSubstitution SubstituteWithOffsets(string sql, List<QueryParamEntry> parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceOffsets = new List<SqlReplacement>();
        if (parameters.Count == 0 || SqlScanCancellation.IsWhiteSpace(sql, cancellationToken))
        {
            return new SqlSubstitution(sql, new SubstitutionOffsets(sourceOffsets, sql.Length, sql.Length));
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!replacements.ContainsKey(parameter.Placeholder))
                replacements.Add(parameter.Placeholder, FormatLiteral(parameter.Value, parameter.DataType, cancellationToken));
        }
        var lookup = replacements.GetAlternateLookup<ReadOnlySpan<char>>();
        var ignoredRanges = GetIgnoredRanges(sql, cancellationToken);
        var ignoredIndex = 0;
        var copiedThrough = 0;
        StringBuilder? output = null;
        foreach (var match in SqlParameterTokens.Scan(sql, cancellationToken))
        {
            while (ignoredIndex < ignoredRanges.Count && ignoredRanges[ignoredIndex].End < match.Start) ignoredIndex++;
            if (ignoredIndex < ignoredRanges.Count && ignoredRanges[ignoredIndex].Start <= match.Start) continue;
            if (match.Style == ParameterPlaceholderStyle.PositionalDollar && match.Start + match.Length < sql.Length &&
                SqlParameterTokens.IsWord(sql[match.Start + match.Length])) continue;
            if (!lookup.TryGetValue(sql.AsSpan(match.Start, match.Length), out var literal)) continue;

            output ??= new StringBuilder(sql.Length);
            AppendText(sql, copiedThrough, match.Start - copiedThrough);
            sourceOffsets.Add(new(output.Length, match.Start, match.Length, literal));
            AppendText(literal, 0, literal.Length);
            copiedThrough = match.Start + match.Length;
        }
        if (output == null)
        {
            return new SqlSubstitution(sql, new SubstitutionOffsets(sourceOffsets, sql.Length, sql.Length));
        }
        AppendText(sql, copiedThrough, sql.Length - copiedThrough);
        cancellationToken.ThrowIfCancellationRequested();
        return new SqlSubstitution(output.ToString(), new SubstitutionOffsets(sourceOffsets, sql.Length, output.Length));
        void AppendText(string text, int start, int length)
        {
            for (var i = 0; i < length; i += 4096)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output!.Append(text, start + i, Math.Min(4096, length - i));
            }
        }
    }

    private static List<(int Start, int End)> GetIgnoredRanges(string sql, CancellationToken cancellationToken)
    {
        var ranges = new List<(int Start, int End)>();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        int start = -1;

        for (int i = 0; i < sql.Length; i++)
        {
            SqlScanCancellation.Check(cancellationToken, i);
            if (inLineComment)
            {
                if (sql[i] == '\n')
                {
                    ranges.Add((start, i));
                    inLineComment = false;
                    start = -1;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/')
                {
                    ranges.Add((start, i + 1));
                    inBlockComment = false;
                    start = -1;
                    i++;
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                }
                else if (sql[i] == '\'')
                {
                    ranges.Add((start, i));
                    inSingleQuote = false;
                    start = -1;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (sql[i] == '"' && i + 1 < sql.Length && sql[i + 1] == '"')
                {
                    i++;
                }
                else if (sql[i] == '"')
                {
                    ranges.Add((start, i));
                    inDoubleQuote = false;
                    start = -1;
                }
                continue;
            }

            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                inLineComment = true;
                start = i;
                i++;
            }
            else if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                inBlockComment = true;
                start = i;
                i++;
            }
            else if (sql[i] == '\'')
            {
                inSingleQuote = true;
                start = i;
            }
            else if (sql[i] == '"')
            {
                inDoubleQuote = true;
                start = i;
            }
        }

        if (start != -1) ranges.Add((start, sql.Length - 1));
        return ranges;
    }

    private static string FormatLiteral(object? value, string dataType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value == null) return "NULL";
        var text = value.ToString();
        var type = dataType.ToLowerInvariant();
        if (type is "number" or "int" or "integer" or "decimal" or "float" or "double") return text ?? "0";
        if (type is "boolean" or "bool") return text?.ToLowerInvariant() == "true" ? "1" : "0";
        var literal = new StringBuilder("'");
        text ??= string.Empty;
        for (var i = 0; i < text.Length; i++)
        {
            SqlScanCancellation.Check(token, i);
            literal.Append(text[i]);
            if (text[i] == '\'') literal.Append('\'');
        }
        token.ThrowIfCancellationRequested();
        return literal.Append('\'').ToString();
    }
}
