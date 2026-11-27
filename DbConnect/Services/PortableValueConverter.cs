using System;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A value that the target column cannot hold; the message names the column and the value's problem.</summary>
public sealed class DataTransferValueException(string message) : Exception(message);

/// <summary>
/// Converts the values one engine's driver reads into values another engine's driver writes into a column of the same
/// portable type: SQLite's text dates become dates, MySQL's 0/1 booleans become booleans, DuckDB's lists and structs
/// become JSON text, instants become UTC where the target keeps no offset, and engines that store a type as text
/// (SQLite, and MySQL for uuids and intervals) get its text form. A value that cannot be represented throws
/// <see cref="DataTransferValueException"/> naming the column, so its row fails or is skipped, never written wrong.
/// </summary>
public static class PortableValueConverter
{
    public static Func<object?, object?> For(PortableColumn column, string targetProvider) => value =>
    {
        if (value is null or DBNull) return null;
        try { return Convert(value, column.Type, targetProvider); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new DataTransferValueException($"Column {column.Name}: the {value.GetType().Name} value {Show(value)} cannot be written as {column.Type}: {ex.Message}");
        }
    };

    private static bool StoresAsText(string provider, PortableType type) => (provider, type) switch
    {
        ("SQLite", PortableType.Date or PortableType.Time or PortableType.Timestamp or PortableType.TimestampTz or PortableType.Interval or PortableType.Uuid) => true,
        ("MySQL", PortableType.Interval or PortableType.Uuid) => true,
        ("SQL Server", PortableType.Interval) => true,
        _ => false
    };

    private static object Convert(object value, PortableType type, string provider)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case PortableType.Boolean:
                return value switch
                {
                    bool b => b,
                    string s => s.Trim().ToLowerInvariant() switch
                    {
                        "true" or "t" or "1" or "yes" => true,
                        "false" or "f" or "0" or "no" => false,
                        _ => throw new FormatException("it is not a boolean")
                    },
                    _ => System.Convert.ToDecimal(value, inv) != 0
                };
            case PortableType.Int8 or PortableType.Int16:
                return checked((short)Whole(value, type == PortableType.Int8 ? sbyte.MinValue : short.MinValue, type == PortableType.Int8 ? sbyte.MaxValue : short.MaxValue));
            case PortableType.Int32:
                return checked((int)Whole(value, int.MinValue, int.MaxValue));
            case PortableType.Int64:
                return Whole(value, long.MinValue, long.MaxValue);
            case PortableType.Decimal:
                return value switch
                {
                    BigInteger big => (decimal)big,
                    string s => decimal.Parse(s, NumberStyles.Float, inv),
                    _ => System.Convert.ToDecimal(value, inv)
                };
            case PortableType.Single:
                return value is string fs ? float.Parse(fs, NumberStyles.Float, inv) : System.Convert.ToSingle(value, inv);
            case PortableType.Double:
                return value is string ds ? double.Parse(ds, NumberStyles.Float, inv) : System.Convert.ToDouble(value, inv);
            case PortableType.Char or PortableType.VarChar or PortableType.Text:
                return Text(value);
            case PortableType.Binary or PortableType.VarBinary or PortableType.Blob:
                return value switch
                {
                    byte[] bytes => bytes,
                    Guid guid => guid.ToByteArray(),
                    _ => throw new InvalidCastException("it is not binary data")
                };
            case PortableType.Date:
                var date = Instant(value).DateTime.Date;
                return StoresAsText(provider, type) ? date.ToString("yyyy-MM-dd", inv)
                    : provider == "PostgreSQL" ? DateOnly.FromDateTime(date) : DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            case PortableType.Time:
                var time = value switch
                {
                    TimeSpan span => span,
                    TimeOnly only => only.ToTimeSpan(),
                    string s => TimeSpan.Parse(s, inv),
                    _ => Instant(value).DateTime.TimeOfDay
                };
                if (StoresAsText(provider, type)) return time.ToString(@"hh\:mm\:ss\.FFFFFFF", inv).TrimEnd('.');
                if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) throw new OverflowException("it is not a time of day");
                return provider is "PostgreSQL" or "DuckDB" ? TimeOnly.FromTimeSpan(time) : time;
            case PortableType.Timestamp:
                var local = DateTime.SpecifyKind(value is DateTime d ? d : Instant(value).DateTime, DateTimeKind.Unspecified);
                return StoresAsText(provider, type) ? local.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", inv).TrimEnd('.') : local;
            case PortableType.TimestampTz:
                var instant = Instant(value);
                if (StoresAsText(provider, type)) return instant.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", inv);
                return provider switch
                {
                    "SQL Server" => instant,
                    "MySQL" => DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified),
                    _ => instant.UtcDateTime
                };
            case PortableType.Interval:
                var interval = value switch
                {
                    TimeSpan span => (object)span,
                    string s when TimeSpan.TryParse(s, inv, out var parsed) => parsed,
                    _ => Text(value)
                };
                return StoresAsText(provider, type) || interval is string ? interval is TimeSpan t ? t.ToString("c", inv) : interval : interval;
            case PortableType.Uuid:
                var uuid = value switch
                {
                    Guid g => g,
                    byte[] { Length: 16 } bytes => new Guid(bytes),
                    _ => Guid.Parse(Text(value))
                };
                return StoresAsText(provider, type) ? uuid.ToString("D") : uuid;
            default:
                return value is string json ? json : JsonSerializer.Serialize(Plain(value));
        }
    }

    private static long Whole(object value, long min, long max)
    {
        var number = value switch
        {
            bool b => b ? 1m : 0m,
            BigInteger big => (decimal)big,
            string s => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
        if (number != decimal.Truncate(number)) throw new FormatException("it is not a whole number");
        if (number < min || number > max) throw new OverflowException($"it is outside {min}..{max}");
        return (long)number;
    }

    private static DateTimeOffset Instant(object value) => value switch
    {
        DateTimeOffset offset => offset,
        // Drivers read instants as UTC, some (DuckDB) without saying so; plain timestamps keep their clock time.
        DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        _ => throw new InvalidCastException("it is not a date or time")
    };

    private static string Text(object value) => value switch
    {
        string s => s,
        Guid g => g.ToString("D"),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        byte[] => throw new InvalidCastException("it is binary data, not text"),
        IEnumerable and not string => JsonSerializer.Serialize(Plain(value)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>Lists, arrays and maps as JSON can write them.</summary>
    private static object? Plain(object? value) => value switch
    {
        null or DBNull => null,
        string or bool => value,
        IDictionary map => System.Linq.Enumerable.ToDictionary(System.Linq.Enumerable.Cast<DictionaryEntry>(map),
            e => System.Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "", e => Plain(e.Value)),
        byte[] bytes => System.Convert.ToBase64String(bytes),
        IEnumerable items => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<object?>(items), Plain)),
        BigInteger big => big.ToString(CultureInfo.InvariantCulture),
        _ => value
    };

    private static string Show(object value)
    {
        var text = value is byte[] bytes ? $"<{bytes.Length} bytes>" : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.Length <= 40 ? text : text[..40] + "…";
    }
}
