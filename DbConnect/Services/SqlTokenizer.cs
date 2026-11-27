using System;
using System.Collections.Generic;
using System.Threading;

namespace DbConnect.Services;

internal enum SqlTokenKind { Word, Number, Quoted, Comment, Directive, Symbol }
internal readonly record struct SqlTokenRange(SqlTokenKind Kind, int Start, int Length, bool IsTerminated = true);
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Start, bool IsTerminated = true);

/// <summary>Separates SQL syntax from quoted text and comments.</summary>
internal static class SqlTokenizer
{
    public static IReadOnlyList<SqlToken> Tokenize(string sql, SqlBatchDialect dialect, CancellationToken cancellationToken = default)
    {
        var tokens = new List<SqlToken>();
        foreach (var token in EnumerateRanges(sql, dialect, cancellationToken))
            tokens.Add(new(token.Kind, sql.Substring(token.Start, token.Length), token.Start, token.IsTerminated));
        return tokens;
    }

    /// <summary>Scans token offsets without copying token text or retaining a token list.</summary>
    public static IEnumerable<SqlTokenRange> EnumerateRanges(string sql, SqlBatchDialect dialect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delimiter = ";";
        for (var i = 0; i < sql.Length;)
        {
            SqlScanCancellation.Check(cancellationToken, i);
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            var start = i;
            var ch = sql[i];
            if (dialect == SqlBatchDialect.SqlServer && ch is 'G' or 'g' &&
                SqlBatchParser.IsAtLineStart(sql, i) && SqlBatchParser.TryReadGoLine(sql, i, out var goLength, out _))
            {
                i += goLength;
                yield return new(SqlTokenKind.Directive, start, i - start);
                continue;
            }
            if (dialect == SqlBatchDialect.MySql && ch is 'D' or 'd' &&
                SqlBatchParser.IsAtLineStart(sql, i) &&
                SqlBatchParser.TryReadDelimiterCommand(sql, i, out var newDelimiter, out var delimiterLength))
            {
                delimiter = newDelimiter;
                i += delimiterLength;
                yield return new(SqlTokenKind.Directive, start, i - start);
                continue;
            }
            if (ch == '-' && At(sql, i + 1, '-') || dialect == SqlBatchDialect.MySql && ch == '#')
            {
                i += ch == '#' ? 1 : 2;
                while (i < sql.Length && sql[i] != '\n') { SqlScanCancellation.Check(cancellationToken, i); i++; }
                yield return new(SqlTokenKind.Comment, start, i - start);
                continue;
            }
            if (ch == '/' && At(sql, i + 1, '*'))
            {
                i += 2;
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    SqlScanCancellation.Check(cancellationToken, i);
                    if (At(sql, i, '/') && At(sql, i + 1, '*') && dialect is SqlBatchDialect.PostgreSql or SqlBatchDialect.SqlServer)
                    { depth++; i += 2; }
                    else if (At(sql, i, '*') && At(sql, i + 1, '/')) { depth--; i += 2; }
                    else i++;
                }
                yield return new(SqlTokenKind.Comment, start, i - start, IsTerminated: depth == 0);
                continue;
            }
            if (ch is '\'' or '"' or '`' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                var closed = false;
                i++;
                while (i < sql.Length)
                {
                    SqlScanCancellation.Check(cancellationToken, i);
                    if (sql[i] == close)
                    {
                        i++;
                        if (At(sql, i, close)) { i++; continue; }
                        closed = true;
                        break;
                    }
                    if (sql[i] == '\\' && i + 1 < sql.Length &&
                        (dialect == SqlBatchDialect.MySql && ch is '\'' or '"' ||
                         ch == '\'' && dialect == SqlBatchDialect.PostgreSql && start > 0 && sql[start - 1] is 'E' or 'e')) { SqlScanCancellation.Check(cancellationToken, i); i++; }
                    i++;
                }
                // A quote that reaches the end of the text without closing is unterminated, like an open block comment.
                yield return new(SqlTokenKind.Quoted, start, i - start, IsTerminated: closed);
                continue;
            }
            if (ch == '$' && dialect == SqlBatchDialect.PostgreSql && TryDollarTag(sql, i, out var tag, cancellationToken))
            {
                i += tag.Length;
                var end = SqlScanCancellation.IndexOf(sql, tag, i, cancellationToken);
                i = end < 0 ? sql.Length : end + tag.Length;
                yield return new(SqlTokenKind.Quoted, start, i - start, IsTerminated: end >= 0);
                continue;
            }
            if (ch is '@' or '$' or ':' && i + 1 < sql.Length &&
                (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_'))
            {
                i += 2;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) { SqlScanCancellation.Check(cancellationToken, i); i++; }
                yield return new(SqlTokenKind.Quoted, start, i - start);
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                i++;
                while (i < sql.Length &&
                    !(dialect == SqlBatchDialect.MySql && delimiter != ";" &&
                      sql.AsSpan(i).StartsWith(delimiter, StringComparison.Ordinal)) &&
                    (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) { SqlScanCancellation.Check(cancellationToken, i); i++; }
                yield return new(SqlTokenKind.Word, start, i - start);
                continue;
            }
            if (char.IsDigit(ch))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '.' or '_')) { SqlScanCancellation.Check(cancellationToken, i); i++; }
                yield return new(SqlTokenKind.Number, start, i - start);
                continue;
            }
            if (i + 2 < sql.Length && IsThreeCharacterSymbol(sql.AsSpan(i, 3)))
            {
                i += 3;
                yield return new(SqlTokenKind.Symbol, start, i - start);
                continue;
            }
            if (i + 1 < sql.Length && IsTwoCharacterSymbol(sql.AsSpan(i, 2)))
            {
                i += 2;
                yield return new(SqlTokenKind.Symbol, start, i - start);
                continue;
            }
            i++;
            yield return new(SqlTokenKind.Symbol, start, i - start);
        }
    }

    private static bool IsThreeCharacterSymbol(ReadOnlySpan<char> symbol) => symbol is "->>" or "#>>";

    private static bool IsTwoCharacterSymbol(ReadOnlySpan<char> symbol) =>
        symbol is "::" or ">=" or "<=" or "<>" or "!=" or "||" or "&&" or "->" or "=>" or "#>";

    private static bool At(string sql, int index, char value) => index < sql.Length && sql[index] == value;

    private static bool TryDollarTag(string sql, int start, out string tag, CancellationToken cancellationToken)
    {
        tag = "";
        if (start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] == '_')) return false;
        var i = start + 1;
        if (i < sql.Length && sql[i] != '$')
        {
            if (!char.IsLetter(sql[i]) && sql[i] != '_') return false;
            while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) { SqlScanCancellation.Check(cancellationToken, i); i++; }
        }
        if (!At(sql, i, '$')) return false;
        tag = sql[start..(i + 1)];
        return true;
    }
}
