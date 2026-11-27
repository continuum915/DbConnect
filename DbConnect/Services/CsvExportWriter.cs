using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class CsvExportWriter
{
    public static async Task WriteAsync(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        ExportOptions options, Action rowWritten, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
        var separator = options.Delimiter.ToSeparator();
        if (options.IncludeHeaders)
            await WriteRecordAsync(writer, columns.Select(c => c.Name), separator, cancellationToken);
        await foreach (var row in source.ReadRowsAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRecordAsync(writer, columns.Select((_, i) => Text(i < row.Count ? row[i] : null, options.NullRepresentation)), separator, cancellationToken);
            rowWritten();
        }
        await writer.FlushAsync(cancellationToken);
    }

    public static async Task WriteRecordAsync(StreamWriter writer, IEnumerable<string> values, char delimiter, CancellationToken token)
    {
        var record = string.Join(delimiter, values.Select(value =>
            value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value));
        await writer.WriteAsync((record + "\r\n").AsMemory(), token);
    }

    public static string Text(object? value, NullRepresentation nulls) => value switch
    {
        null or DBNull => nulls switch { NullRepresentation.BackslashN => "\\N", NullRepresentation.NullLiteral => "NULL", _ => "" },
        string text => text,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        Guid guid => guid.ToString("D"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException("Unsupported CSV value type.")
    };
}
