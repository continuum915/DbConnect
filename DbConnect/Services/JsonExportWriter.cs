using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class JsonExportValueException(long row, int column) : Exception
{
    public override string Message => $"Unsupported JSON value at row {row}, column {column}.";
}

internal static class JsonExportWriter
{
    public static string[] Keys(IEnumerable<string> names)
    {
        var originals = names.ToArray();
        var reserved = originals.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        return originals.Select(name =>
        {
            if (used.Add(name)) return name;
            var suffix = 2;
            string key;
            do { key = $"{name} ({suffix++})"; } while (reserved.Contains(key) || !used.Add(key));
            return key;
        }).ToArray();
    }

    public static async Task WriteAsync(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        bool lines, Action rowWritten, CancellationToken token)
    {
        var keys = Keys(columns.Select(c => c.Name));
        using var writer = new Utf8JsonWriter(stream);
        if (!lines) writer.WriteStartArray();
        long rowNumber = 0;
        await foreach (var row in source.ReadRowsAsync(token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            rowNumber++;
            writer.WriteStartObject();
            for (var i = 0; i < keys.Length; i++)
            {
                writer.WritePropertyName(keys[i]);
                try { WriteValue(writer, i < row.Count ? row[i] : null); }
                catch (Exception error) when (error is not OperationCanceledException and not IOException)
                { throw new JsonExportValueException(rowNumber, i + 1); }
            }
            writer.WriteEndObject();
            await writer.FlushAsync(token);
            if (lines)
            {
                await stream.WriteAsync(new byte[] { 10 }, token);
                writer.Reset(stream);
            }
            rowWritten();
        }
        if (!lines) writer.WriteEndArray();
        await writer.FlushAsync(token);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case DateTime date: writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset date: writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateOnly date: writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case TimeOnly time: writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan time: writer.WriteStringValue(time.ToString("c", CultureInfo.InvariantCulture)); break;
            case Guid guid: writer.WriteStringValue(guid); break;
            case JsonElement element: element.WriteTo(writer); break;
            case JsonDocument document: document.RootElement.WriteTo(writer); break;
            case JsonNode node: node.WriteTo(writer); break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double:
                JsonSerializer.Serialize(writer, value, value.GetType()); break;
            default: throw new NotSupportedException();
        }
    }
}
