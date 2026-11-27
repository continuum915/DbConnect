using System;
using System.Globalization;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Formats text for result-grid display without changing the underlying cell value. NULL, date/time
/// and binary values follow the Results preferences; everything else uses the invariant formatting
/// that copy and export also use. Copy, export and SQL literals never go through this class.
/// </summary>
public static class GridCellDisplay
{
    private const int HexPreviewBytes = 32;

    public static string FormatValue(object? value) => FormatValue(value, AppPreferences.Results);

    public static string FormatValue(object? value, ResultPreferences display)
    {
        // Bound large strings before the full-value formatter can parse or reserialize JSON.
        var text = value switch
        {
            null or DBNull => display.NullText,
            string large when large.Length > CellViewerLimits.GridDisplayChars => large,
            DateTime dateTime => FormatDate(dateTime, display.DateTimeFormat, "yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dateTimeOffset => FormatDate(dateTimeOffset, WithOffset(display.DateTimeFormat), "yyyy-MM-dd HH:mm:ss zzz"),
            byte[] bytes => FormatBinary(bytes, display.BinaryDisplay),
            _ => QueryResultFormattingService.FormatValue(value)
        };
        var length = Math.Min(text.Length, CellViewerLimits.GridDisplayChars);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;

        var prefix = text[..length].Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return length < text.Length ? prefix + "…" : prefix;
    }

    private static string FormatBinary(byte[] bytes, BinaryDisplayMode mode)
    {
        if (mode != BinaryDisplayMode.Hex) return $"<{bytes.Length} bytes>";
        var shown = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, HexPreviewBytes));
        return bytes.Length > HexPreviewBytes ? $"0x{shown}…" : $"0x{shown}";
    }

    /// <summary>Keeps the UTC offset visible for offset values unless the format already shows one.</summary>
    private static string WithOffset(string format) => format.Contains('z') ? format : format + " zzz";

    private static string FormatDate(IFormattable value, string format, string fallback)
    {
        try { return value.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return value.ToString(fallback, CultureInfo.InvariantCulture); }
    }
}
