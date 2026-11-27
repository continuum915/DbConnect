using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-07 — Converts between the text a cell editor shows and the typed CLR value that
/// goes into <see cref="EditableRow.Values"/> / becomes a SQL parameter, per column type.
///
/// Two invariants this exists to protect:
///  - Parsing never depends on the thread's current culture. "1.5" always means one and a half,
///    never one-thousand-five, no matter which locale the app is running under. A lone comma
///    (as in "1,5") is treated as the decimal separator rather than silently discarded as a
///    thousands grouping character, which is how ordinary <see cref="decimal.TryParse"/> with
///    <see cref="NumberStyles.Number"/> would otherwise corrupt it into 15.
///  - NULL and empty string are distinct states. <see cref="Parse"/> returns <c>null</c> only
///    when the caller says the cell is explicitly NULL (see the NULL toggle in
///    TableDataTabView); an empty text box on a Text column parses to <c>""</c>, not <c>null</c>.
/// </summary>
public static class CellValueConverter
{
    /// <summary>
    /// Parses editor text into a typed CLR value for <paramref name="type"/>. Returns
    /// <c>null</c> only for a Text column parsing an actually-null string, never for empty
    /// input on non-text types (those throw <see cref="FormatException"/> instead so a bad
    /// parse can be rejected rather than silently coerced to NULL).
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid value for <paramref name="type"/>.</exception>
    public static object? Parse(string? text, ColumnDataType type)
    {
        // Unknown means the column couldn't be classified (e.g. a SQLite/DuckDB column with no
        // type affinity) — treat it as free text rather than rejecting an empty edit outright.
        if (type == ColumnDataType.Text || type == ColumnDataType.Unknown)
            return text ?? string.Empty;

        if (string.IsNullOrEmpty(text))
            throw new FormatException($"A value is required for a {type} column; use the NULL toggle to clear it instead.");

        return type switch
        {
            ColumnDataType.Boolean => ParseBoolean(text),
            ColumnDataType.Integer => ParseInteger(text),
            ColumnDataType.Real => ParseReal(text),
            ColumnDataType.Decimal => ParseDecimal(text),
            ColumnDataType.Date => ParseDate(text),
            ColumnDataType.DateTime => ParseDateTime(text),
            ColumnDataType.Guid => Guid.Parse(text.Trim()),
            ColumnDataType.Json => ParseJson(text),
            _ => text
        };
    }

    /// <summary>
    /// A type-correct placeholder for "not NULL, but nothing typed yet" — used when the NULL
    /// toggle is switched off, so the stored value is always a valid CLR value for the column's
    /// type rather than a bare "" that would fail type conversion for a non-Text column.
    /// </summary>
    public static object DefaultNonNullValue(ColumnDataType type) => type switch
    {
        ColumnDataType.Boolean => false,
        ColumnDataType.Integer => 0L,
        ColumnDataType.Real => 0.0,
        ColumnDataType.Decimal => 0m,
        ColumnDataType.Date => DateTime.Today,
        ColumnDataType.DateTime => DateTime.Now,
        ColumnDataType.Guid => Guid.Empty,
        ColumnDataType.Blob => Array.Empty<byte>(),
        ColumnDataType.Json => "{}",
        _ => string.Empty
    };

    /// <summary>
    /// Normalizes a raw value read straight off the database into the canonical CLR type for
    /// <paramref name="type"/>. Needed because SQLite's CLR type reflects the value's storage
    /// class, not the column's declared type: a column declared <c>DATE</c>/<c>DATETIME</c> comes
    /// back from Microsoft.Data.Sqlite as a plain <see cref="string"/> (e.g. <c>"2020-01-01"</c>),
    /// <c>BOOLEAN</c> as a <see cref="long"/> (<c>1</c>/<c>0</c>), and <c>DECIMAL</c> as a
    /// <see cref="double"/> — verified empirically against Microsoft.Data.Sqlite. Editors that
    /// pattern-match on the expected CLR type (the DatePicker/TimePicker/tri-state CheckBox in
    /// TableDataTabView) call this first so a populated SQLite cell doesn't render blank/unchecked.
    /// Returns the input unchanged for a type/value combination it doesn't need to coerce.
    /// </summary>
    public static object? Coerce(object? raw, ColumnDataType type)
    {
        if (raw == null) return null;

        return type switch
        {
            ColumnDataType.Boolean => raw switch
            {
                bool => raw,
                string s => ParseBoolean(s),
                _ => Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0
            },
            ColumnDataType.Date => CoerceDateTime(raw)?.Date,
            ColumnDataType.DateTime => CoerceDateTime(raw),
            ColumnDataType.Decimal => raw switch
            {
                decimal => raw,
                string s => ParseDecimal(s),
                _ => Convert.ToDecimal(raw, CultureInfo.InvariantCulture)
            },
            ColumnDataType.Guid => raw switch
            {
                Guid => raw,
                string s => Guid.Parse(s.Trim()),
                _ => raw
            },
            _ => raw
        };
    }

    private static DateTime? CoerceDateTime(object raw) => raw switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.DateTime,
        string s => ParseDateTime(s),
        _ => null
    };

    /// <summary>Formats a typed CLR value for display/editing, culture-invariant. NULL formats to "".</summary>
    public static string FormatForEditor(object? value, ColumnDataType type)
    {
        if (value == null) return string.Empty;

        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            DateTime dt when type == ColumnDataType.Date => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            float or double => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            Guid g => g.ToString(),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static bool ParseBoolean(string text)
    {
        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out var value)) return value;
        if (trimmed == "1") return true;
        if (trimmed == "0") return false;
        throw new FormatException($"'{text}' is not a valid boolean.");
    }

    private static long ParseInteger(string text)
    {
        var normalized = NormalizeNumericSeparators(text);
        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid integer.");
    }

    private static double ParseReal(string text)
    {
        var normalized = NormalizeNumericSeparators(text);
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid number.");
    }

    private static decimal ParseDecimal(string text)
    {
        var normalized = NormalizeNumericSeparators(text);
        if (decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid decimal.");
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    private static DateTime ParseDate(string text)
    {
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return value.Date;
        throw new FormatException($"'{text}' is not a valid date (expected yyyy-MM-dd).");
    }

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd"
    ];

    private static DateTime ParseDateTime(string text)
    {
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid date/time (expected yyyy-MM-dd HH:mm:ss).");
    }

    /// <summary>
    /// Validates that <paramref name="text"/> is well-formed JSON before it can be saved (RAIN-P2-09).
    /// The original text is returned unchanged — not re-serialized — so formatting the user typed
    /// is preserved rather than silently reflowed.
    /// </summary>
    private static string ParseJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"'{text}' is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes "," / "." usage to the invariant convention (dot = decimal separator) so a
    /// numeric parse never depends on which locale produced the text:
    ///  - a single comma with no dot ("1,5") is a decimal comma -> "1.5"
    ///  - commas alongside a dot ("1,234.56") are thousands grouping -> stripped
    ///  - more than one comma with no dot ("1,234,567") is thousands grouping -> stripped
    ///  - anything else (a lone dot, or no separators) is already invariant-shaped
    /// </summary>
    private static string NormalizeNumericSeparators(string text)
    {
        var trimmed = text.Trim();
        var commaCount = trimmed.Count(c => c == ',');
        var dotCount = trimmed.Count(c => c == '.');

        if (commaCount == 1 && dotCount == 0)
            return trimmed.Replace(',', '.');

        if (commaCount > 0)
            return trimmed.Replace(",", "");

        return trimmed;
    }
}
