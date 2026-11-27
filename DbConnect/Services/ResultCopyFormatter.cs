using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public enum CopyQuoteCharacter { None, DoubleQuote, SingleQuote }

/// <summary>How result rows are written to the clipboard. The defaults reproduce plain tab-separated copy.</summary>
public sealed record ResultCopyOptions
{
    public bool IncludeHeader { get; init; } = true;
    public CsvDelimiter Delimiter { get; init; } = CsvDelimiter.Tab;
    public CopyQuoteCharacter Quote { get; init; } = CopyQuoteCharacter.None;
    /// <summary>Quote every header and value, not only those containing the delimiter, the quote or a line break.</summary>
    public bool AlwaysQuote { get; init; }
    /// <summary>Write values as SQL literals (<c>'text'</c>, <c>42</c>, <c>NULL</c>); the quote character then applies to headers only.</summary>
    public bool SqlLiterals { get; init; }
    /// <summary>The connection whose boolean and binary literal style SQL literals follow; null writes TRUE/FALSE and X'..'.</summary>
    public DatabaseProviderCapabilities? Dialect { get; init; }
}

/// <summary>
/// RAIN-P2-17 — Formats result values for the clipboard from their raw values, so SQL literals can
/// tell a number from text and SQL NULL from the string "NULL".
/// </summary>
public static class ResultCopyFormatter
{
    public static string FormatRows(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows,
        ResultCopyOptions options)
    {
        var delimiter = options.Delimiter.ToSeparator().ToString();
        var lines = new List<string>();
        if (options.IncludeHeader)
            lines.Add(string.Join(delimiter, headers.Select(header => Quote(header, delimiter, options))));

        foreach (var row in rows)
            lines.Add(string.Join(delimiter, Enumerable.Range(0, headers.Count)
                .Select(i => FormatValue(i < row.Count ? row[i] : null, delimiter, options))));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A single cell has no delimiter around it, so only <see cref="ResultCopyOptions.AlwaysQuote"/> quotes it.</summary>
    public static string FormatCell(object? value, ResultCopyOptions options) => FormatValue(value, delimiter: null, options);

    private static string FormatValue(object? value, string? delimiter, ResultCopyOptions options) =>
        options.SqlLiterals
            ? SqlLiteral.Format(value, options.Dialect)
            : Quote(QueryResultFormattingService.FormatValue(value), delimiter, options);

    private static string Quote(string text, string? delimiter, ResultCopyOptions options)
    {
        if (options.Quote == CopyQuoteCharacter.None)
            return text;

        var quote = options.Quote == CopyQuoteCharacter.DoubleQuote ? "\"" : "'";
        var needsQuoting = options.AlwaysQuote
            || (delimiter != null && (text.Contains(delimiter) || text.Contains(quote) || text.Contains('\n') || text.Contains('\r')));

        return needsQuoting ? quote + text.Replace(quote, quote + quote) + quote : text;
    }
}
