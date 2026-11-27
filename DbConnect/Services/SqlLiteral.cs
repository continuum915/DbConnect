using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Globalization;

namespace DbConnect.Services;

/// <summary>
/// Writes a value as an inline SQL literal, for SQL the user copies or runs as a script rather than
/// SQL executed with bound parameters.
/// </summary>
public static class SqlLiteral
{
    /// <summary>
    /// A literal in <paramref name="dialect"/>'s string, boolean and binary style; the rest is common to every
    /// supported dialect. Without a dialect, booleans are TRUE/FALSE and binary is <c>X'..'</c>. A value
    /// with no literal form is quoted as its display text.
    /// </summary>
    public static string Format(object? value, DatabaseProviderCapabilities? dialect = null) =>
        TryFormat(value, dialect, out var literal) ? literal : FormatText(QueryResultFormattingService.FormatValue(value), dialect);

    /// <summary>
    /// The literal for <paramref name="value"/>, or false when its type has no faithful literal form
    /// (scripts that must re-run unchanged fail instead of writing display text).
    /// </summary>
    public static bool TryFormat(object? value, DatabaseProviderCapabilities? dialect, [NotNullWhen(true)] out string? literal)
    {
        literal = value switch
        {
            null or DBNull => "NULL",
            string text => FormatText(text, dialect),
            bool boolean when dialect?.UsesBooleanKeywords ?? true => boolean ? "TRUE" : "FALSE",
            bool boolean => boolean ? "1" : "0",
            byte[] bytes => FormatBinary(bytes, dialect),
            ReadOnlyMemory<byte> bytes => FormatBinary(bytes.ToArray(), dialect),
            Memory<byte> bytes => FormatBinary(bytes.ToArray(), dialect),
            double number when !double.IsFinite(number) => Quote(number.ToString(CultureInfo.InvariantCulture)),
            float number when !float.IsFinite(number) => Quote(number.ToString(CultureInfo.InvariantCulture)),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            BigInteger number => number.ToString(CultureInfo.InvariantCulture),
            // Full precision: the grid's display format drops fractional seconds. A UTC value comes from a
            // time-zone-aware column, so it keeps its offset instead of being read in the session's zone.
            DateTime { Kind: DateTimeKind.Utc } dateTime => Quote(Timestamp(dateTime, dialect) + "+00:00"),
            DateTime dateTime => Quote(Timestamp(dateTime, dialect)),
            DateTimeOffset dateTimeOffset => Quote(Timestamp(dateTimeOffset.DateTime, dialect) + dateTimeOffset.ToString("zzz", CultureInfo.InvariantCulture)),
            DateOnly date => Quote(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            TimeOnly time => Quote(time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
            // A time of day; longer or negative spans only where TIME is a duration (MySQL). Other intervals
            // have no literal common to the dialects.
            TimeSpan time when time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
                => Quote(time.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.')),
            TimeSpan duration when dialect?.TimeLiteralAcceptsDurations == true => Quote(Duration(duration)),
            Guid guid => Quote(guid.ToString("D")),
            _ => null
        };
        return literal != null;
    }

    private static string Timestamp(DateTime dateTime, DatabaseProviderCapabilities? dialect) =>
        dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (dialect?.DateTimeLiteralSeparator ?? ' ') +
        dateTime.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    /// <summary><c>[-]h:mm:ss[.fffffff]</c> with the hours unbounded, e.g. <c>-838:59:59</c>.</summary>
    private static string Duration(TimeSpan span) =>
        (span < TimeSpan.Zero ? "-" : "") + ((long)Math.Abs(span.TotalHours)).ToString(CultureInfo.InvariantCulture) +
        span.ToString(@"\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');

    private static string FormatBinary(byte[] bytes, DatabaseProviderCapabilities? dialect) =>
        dialect?.FormatBinaryLiteral(bytes) ?? $"X'{Convert.ToHexString(bytes)}'";

    private static string FormatText(string text, DatabaseProviderCapabilities? dialect) => dialect?.FormatStringLiteral(text) ?? Quote(text);

    private static string Quote(string text) => $"'{text.Replace("'", "''")}'";
}
