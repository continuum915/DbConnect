using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Writes the recovery schema without buffering an entire JSON document or SQL string value.</summary>
internal static class RecoverySnapshotWriter
{
    internal const int StringSegmentCharacters = 16_384;

    internal static async Task WriteAsync(Stream destination, SessionSnapshot snapshot)
    {
        await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        // Preserve the default serializer's property order, nulls, escaping, and timestamp/GUID formats.
        // Contract tests compare the complete bytes with JsonSerializer for the snapshot models.
        writer.WriteStartObject();
        writer.WriteNumber(nameof(SessionSnapshot.Pid), snapshot.Pid);
        writer.WriteString(nameof(SessionSnapshot.Timestamp), snapshot.Timestamp);
        writer.WritePropertyName(nameof(SessionSnapshot.Tabs));
        if (snapshot.Tabs == null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var tab in snapshot.Tabs)
            {
                if (tab == null)
                {
                    writer.WriteNullValue();
                    await writer.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                writer.WriteStartObject();
                await WriteStringAsync(writer, nameof(TabSnapshot.Title), tab.Title).ConfigureAwait(false);
                await WriteStringAsync(writer, nameof(TabSnapshot.QueryText), tab.QueryText).ConfigureAwait(false);
                await WriteStringAsync(writer, nameof(TabSnapshot.SessionName), tab.SessionName).ConfigureAwait(false);
                if (tab.ConnectionId is { } connectionId) writer.WriteString(nameof(TabSnapshot.ConnectionId), connectionId);
                else writer.WriteNull(nameof(TabSnapshot.ConnectionId));
                await WriteStringAsync(writer, nameof(TabSnapshot.FilePath), tab.FilePath).ConfigureAwait(false);
                writer.WriteBoolean(nameof(TabSnapshot.IsDirty), tab.IsDirty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        await WriteStringAsync(writer, nameof(SessionSnapshot.ActiveTabTitle), snapshot.ActiveTabTitle).ConfigureAwait(false);
        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteStringAsync(Utf8JsonWriter writer, string property, string? value)
    {
        writer.WritePropertyName(property);
        if (value == null) writer.WriteNullValue();
        else if (value.Length <= StringSegmentCharacters) writer.WriteStringValue(value);
        else
        {
            for (var offset = 0; offset < value.Length;)
            {
                var length = Math.Min(StringSegmentCharacters, value.Length - offset);
                // Keep valid UTF-16 pairs together at segment boundaries.
                if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1]) &&
                    char.IsLowSurrogate(value[offset + length])) length--;
                writer.WriteStringValueSegment(value.AsSpan(offset, length), offset + length == value.Length);
                offset += length;
                // Flush even in the middle of a JSON string, bounding the writer's retained buffer.
                await writer.FlushAsync().ConfigureAwait(false);
            }
            return;
        }
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
