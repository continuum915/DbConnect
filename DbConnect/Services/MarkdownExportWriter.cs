using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class MarkdownExportWriter
{
    public static async Task WriteAsync(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        ExportOptions options, Action rowWritten, CancellationToken token)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
        if (columns.Count > 0)
        {
            await WriteRecordAsync(writer, columns.Select(c => options.IncludeHeaders ? c.Name : ""), token);
            await WriteRecordAsync(writer, columns.Select(_ => "---"), token);
        }
        await foreach (var row in source.ReadRowsAsync(token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (columns.Count > 0)
                await WriteRecordAsync(writer, columns.Select((_, i) => CsvExportWriter.Text(i < row.Count ? row[i] : null, options.NullRepresentation)), token);
            rowWritten();
        }
        await writer.FlushAsync(token);
    }

    private static async Task WriteRecordAsync(StreamWriter writer, IEnumerable<string> values, CancellationToken token)
    {
        await writer.WriteAsync("| ".AsMemory(), token);
        var first = true;
        foreach (var value in values)
        {
            if (!first) await writer.WriteAsync(" | ".AsMemory(), token);
            first = false;
            var escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            foreach (var character in new[] { '\\', '|', '`', '*', '_', '[', ']' })
                escaped = escaped.Replace(character.ToString(), "\\" + character);
            escaped = escaped.Replace("\r\n", "<br>").Replace("\r", "<br>").Replace("\n", "<br>");
            await writer.WriteAsync(escaped.AsMemory(), token);
        }
        await writer.WriteAsync(" |\n".AsMemory(), token);
    }
}
