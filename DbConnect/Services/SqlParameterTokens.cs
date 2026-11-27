using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DbConnect.Models;

namespace DbConnect.Services;

internal readonly record struct SqlParameterToken(int Start, int Length, ParameterPlaceholderStyle Style);

/// <summary>Forward placeholder scan with cancellation even inside unusually long names.</summary>
internal static class SqlParameterTokens
{
    internal static IEnumerable<SqlParameterToken> Scan(string sql, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (var i = 0; i < sql.Length; i++)
        {
            SqlScanCancellation.Check(token, i);
            var start = i;
            var ch = sql[i];
            ParameterPlaceholderStyle style;
            if (ch is '@' or ':' && (ch != ':' || i == 0 || sql[i - 1] != ':') &&
                i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                style = ch == '@' ? ParameterPlaceholderStyle.NamedAt : ParameterPlaceholderStyle.NamedColon;
                i += 2;
                while (i < sql.Length && IsWord(sql[i]))
                {
                    SqlScanCancellation.Check(token, i);
                    i++;
                }
            }
            else if (ch == '?' || ch == '$' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
            {
                style = ch == '?' ? ParameterPlaceholderStyle.PositionalQuestion : ParameterPlaceholderStyle.PositionalDollar;
                i++;
                while (i < sql.Length && char.IsDigit(sql[i]))
                {
                    SqlScanCancellation.Check(token, i);
                    i++;
                }
            }
            else continue;

            yield return new(start, i - start, style);
            i--;
        }
        token.ThrowIfCancellationRequested();
    }

    // Matches the Unicode categories used by the existing regex \w tokens.
    internal static bool IsWord(char ch) => char.GetUnicodeCategory(ch) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;

    internal static int FirstLocalVariableEnd(string sql, CancellationToken token)
    {
        for (var i = 0; i < sql.Length; i++)
        {
            SqlScanCancellation.Check(token, i);
            var length = sql.AsSpan(i).StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase) ? 7 :
                sql.AsSpan(i).StartsWith("SET", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            if (length == 0 || i + length >= sql.Length || !char.IsWhiteSpace(sql[i + length])) continue;
            var end = i + length;
            while (end < sql.Length && char.IsWhiteSpace(sql[end]))
            {
                SqlScanCancellation.Check(token, end);
                end++;
            }
            // Preserve the detector's existing first-DECLARE/SET-only exclusion contract.
            return i == 0 || !IsWord(sql[i - 1]) ? end : -1;
        }
        return -1;
    }
}
