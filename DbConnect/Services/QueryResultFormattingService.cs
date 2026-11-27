using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DbConnect.Models;

namespace DbConnect.Services;

public static class QueryResultFormattingService
{
    /// <summary>
    /// Builds one display row per <see cref="QueryResult.ObjectRows"/> entry, indexed by ordinal
    /// position so duplicate column names, like <c>SELECT a.id, b.id</c>, stay distinct instead of
    /// one dictionary key overwriting the other. Each row's display strings are formatted lazily on
    /// first access rather than eagerly here, so a virtualizing grid only pays for the cells it renders.
    /// </summary>
    public static IReadOnlyList<QueryResultDisplayRow> BuildDisplayRows(QueryResult result)
    {
        if (result == null || !result.HasResultSet)
            return [];

        var columnCount = result.ColumnInfos.Count;
        return result.ObjectRows
            .Select(row => new QueryResultDisplayRow(row, columnCount))
            .ToList();
    }

    public static string FormatValue(object? value)
    {
        return value switch
        {
            null => "NULL",
            string text => TryFormatJsonText(text),
            bool boolean => boolean ? "TRUE" : "FALSE",
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            byte[] bytes => $"<{bytes.Length} bytes>",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            IEnumerable<KeyValuePair<string, object?>> kvp => JsonSerializer.Serialize(
                kvp.ToDictionary(entry => entry.Key, entry => entry.Value)),
            System.Collections.IEnumerable sequence when value is not string =>
                JsonSerializer.Serialize(sequence.Cast<object?>().ToArray()),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string ValueAt(IReadOnlyList<string> row, int ordinal) =>
        ordinal < row.Count ? row[ordinal] : string.Empty;

    public static string BuildTabSeparated(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<string>> rows,
        bool includeHeaders = true)
    {
        var builder = new StringBuilder();
        if (includeHeaders)
            builder.AppendLine(string.Join('\t', columns));

        foreach (var row in rows)
            builder.AppendLine(string.Join('\t', Enumerable.Range(0, columns.Count).Select(i => ValueAt(row, i))));

        return builder.ToString().TrimEnd();
    }

    public static string BuildCsv(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(EscapeCsv)));
        foreach (var row in rows)
            builder.AppendLine(string.Join(",", Enumerable.Range(0, columns.Count).Select(i => EscapeCsv(ValueAt(row, i)))));
        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }

    public static string BuildCsv(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<string>> rows,
        ExportOptions options)
    {
        char sep = options.Delimiter.ToSeparator();

        var builder = new StringBuilder();
        if (options.IncludeHeaders)
            builder.AppendLine(string.Join(sep, columns.Select(c => EscapeDelimited(c, sep))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(sep, Enumerable.Range(0, columns.Count).Select(i =>
            {
                var val = ValueAt(row, i);
                if (val == "NULL")
                {
                    return options.NullRepresentation switch
                    {
                        NullRepresentation.BackslashN => @"\N",
                        NullRepresentation.NullLiteral => "NULL",
                        _ => string.Empty
                    };
                }
                return EscapeDelimited(val, sep);
            })));
        }
        return builder.ToString();
    }

    public static string BuildJson(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<string>> rows,
        ExportOptions options)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        bool firstRow = true;
        foreach (var row in rows)
        {
            if (!firstRow) builder.Append(',');
            firstRow = false;
            builder.Append('{');
            bool firstCol = true;
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (!firstCol) builder.Append(',');
                firstCol = false;
                builder.Append('"');
                builder.Append(JsonEncode(col));
                builder.Append('"');
                builder.Append(':');
                var val = ValueAt(row, i);
                if (val == "NULL")
                {
                    builder.Append(options.NullRepresentation switch
                    {
                        NullRepresentation.BackslashN => @"""\N""",
                        NullRepresentation.NullLiteral => "\"NULL\"",
                        _ => "null"
                    });
                }
                else
                {
                    builder.Append('"');
                    builder.Append(JsonEncode(val));
                    builder.Append('"');
                }
            }
            builder.Append('}');
        }
        builder.Append(']');
        return builder.ToString();
    }

    private static string EscapeDelimited(string value, char sep)
    {
        if (value.Contains('"') || value.Contains(sep) || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    /// <summary>
    /// Escapes exactly the characters JSON requires (quote, backslash, and every control character,
    /// not just \n/\r/\t) without also escaping HTML-sensitive punctuation the default encoder would —
    /// this output is written to a plain .json file, not embedded in HTML.
    /// </summary>
    private static string JsonEncode(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    private static string TryFormatJsonText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        if ((trimmed[0], trimmed[^1]) is not ('{', '}') and not ('[', ']'))
            return text;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
