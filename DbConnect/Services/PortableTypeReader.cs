using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Reads a column of one engine's table design as a <see cref="PortableColumn"/>: the declared type as each engine
/// spells it (PostgreSQL "character varying(20)", MySQL "bigint unsigned", SQL Server "nvarchar(max)", DuckDB
/// "INTEGER[]", SQLite's free-form names), the default when every engine can write it, and a note for whatever the
/// portable description drops. Types without a portable equivalent are read as text, and say so.
/// </summary>
public static partial class PortableTypeReader
{
    public static PortableColumn Read(string providerName, TableDesignColumn column)
    {
        var notes = new List<string>();
        var spelled = column.DataType.Trim();
        var (type, length, precision, scale) = ReadType(providerName, spelled, notes);
        var identity = column.Identity != null || column.Sequence is { Owned: true };
        var portable = new PortableColumn(column.Name, type, column.IsNullable)
        {
            Length = length,
            Precision = precision,
            Scale = scale,
            IsIdentity = identity,
            Collation = column.Collation,
            SourceType = spelled
        };
        if (!identity && column.Default is { } text)
        {
            var value = ReadDefault(text, type);
            if (value == null) notes.Add($"Default {text.Trim()} is an expression only {providerName} understands; it is not carried over.");
            portable = portable with { Default = value };
        }
        if (column.Generated != null)
            notes.Add($"Generated from {column.Generated.Expression}; the copy is a plain column holding the values.");
        if (column.Collation != null) notes.Add($"Collation {column.Collation} is not carried over.");
        return portable with { Notes = notes.Select(n => $"{column.Name}: {n}").ToList() };
    }

    private static (PortableType Type, int? Length, int? Precision, int? Scale) ReadType(string provider, string spelled, List<string> notes)
    {
        var lower = spelled.ToLowerInvariant();
        if (lower.Length == 0)
        {
            notes.Add("SQLite column without a declared type is read as text.");
            return (PortableType.Text, null, null, null);
        }
        if (lower.EndsWith("[]", StringComparison.Ordinal) || lower.StartsWith("struct(", StringComparison.Ordinal) ||
            lower.StartsWith("map(", StringComparison.Ordinal) || lower.StartsWith("union(", StringComparison.Ordinal))
        {
            notes.Add($"{spelled} values are written as JSON text.");
            return (PortableType.Json, null, null, null);
        }
        var withTimeZone = lower.Contains(" with time zone", StringComparison.Ordinal);
        lower = lower.Replace(" without time zone", "").Replace(" with time zone", "");
        var unsigned = lower.EndsWith(" unsigned", StringComparison.Ordinal) || lower.Contains(" unsigned ", StringComparison.Ordinal);
        lower = lower.Replace(" unsigned", "").Replace(" zerofill", "");
        var match = Spelling().Match(lower);
        var name = match.Groups["name"].Value.Trim();
        var args = match.Groups["args"].Success ? match.Groups["args"].Value : null;
        var numbers = args?.Split(',').Select(a => int.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null).ToList();
        int? First() => numbers is { Count: > 0 } ? numbers[0] : null;
        int? Second() => numbers is { Count: > 1 } ? numbers[1] : null;
        var max = args?.Trim() == "max";

        switch (name)
        {
            case "bool" or "boolean" or "bit" when name != "bit" || provider == "SQL Server" || First() is null or 1:
                return (PortableType.Boolean, null, null, null);
            case "bit":
                notes.Add($"{spelled} is read as bytes.");
                return (PortableType.VarBinary, ((First() ?? 1) + 7) / 8, null, null);
            case "tinyint" when provider == "MySQL" && First() == 1 && !unsigned:
                return (PortableType.Boolean, null, null, null);
            case "tinyint" or "int1":
                return (provider == "SQL Server" || unsigned ? PortableType.Int16 : PortableType.Int8, null, null, null);
            case "utinyint":
                return (PortableType.Int16, null, null, null);
            case "smallint" or "int2" or "smallserial" or "year":
                return (unsigned ? PortableType.Int32 : PortableType.Int16, null, null, null);
            case "usmallint":
                return (PortableType.Int32, null, null, null);
            case "mediumint":
                return (PortableType.Int32, null, null, null);
            case "int" or "integer" or "int4" or "serial" or "signed":
                return (provider == "SQLite" || unsigned ? PortableType.Int64 : PortableType.Int32, null, null, null);
            case "uinteger":
                return (PortableType.Int64, null, null, null);
            case "bigint" or "int8" or "bigserial" or "long" when !unsigned:
                return (PortableType.Int64, null, null, null);
            case "bigint" or "ubigint":
                return (PortableType.Decimal, null, 20, 0);
            case "hugeint" or "uhugeint" or "int128":
                notes.Add($"{spelled} is read as decimal(38,0); values beyond 38 digits do not fit.");
                return (PortableType.Decimal, null, 38, 0);
            case "decimal" or "numeric" or "dec" or "fixed":
                return (PortableType.Decimal, null, First(), First() == null ? null : Second() ?? 0);
            case "money" or "smallmoney":
                notes.Add($"{spelled} is read as decimal(19,4).");
                return (PortableType.Decimal, null, 19, 4);
            case "real" when provider == "SQLite":
                return (PortableType.Double, null, null, null);
            case "real" or "float4":
                return (PortableType.Single, null, null, null);
            case "float" when provider == "SQL Server":
                return (First() is <= 24 ? PortableType.Single : PortableType.Double, null, null, null);
            case "float" when provider is "MySQL" or "DuckDB":
                return (PortableType.Single, null, null, null);
            case "float" or "double" or "double precision" or "float8":
                return (PortableType.Double, null, null, null);
            case "char" or "character" or "nchar" or "bpchar" or "national character":
                return (PortableType.Char, First() ?? 1, null, null);
            case "varchar" or "character varying" or "nvarchar" or "varchar2" or "nvarchar2" or "national character varying" or "string":
                return max || First() == null ? (PortableType.Text, null, null, null) : (PortableType.VarChar, First(), null, null);
            case "text" or "ntext" or "tinytext" or "mediumtext" or "longtext" or "clob":
                return (PortableType.Text, null, null, null);
            case "binary":
                return (PortableType.Binary, First() ?? 1, null, null);
            case "varbinary" or "binary varying":
                return max || First() == null ? (PortableType.Blob, null, null, null) : (PortableType.VarBinary, First(), null, null);
            case "blob" or "bytea" or "image" or "tinyblob" or "mediumblob" or "longblob" or "bytes":
                return (PortableType.Blob, null, null, null);
            case "date":
                return (PortableType.Date, null, null, null);
            case "time" or "timetz":
                if (withTimeZone || name == "timetz") notes.Add($"{spelled} keeps the time of day; the offset is dropped.");
                if (provider == "MySQL") notes.Add("MySQL TIME can hold durations beyond a day, which other engines' time of day cannot.");
                return (PortableType.Time, null, First(), null);
            case "timestamp" when provider == "MySQL":
                notes.Add("MySQL TIMESTAMP is stored in UTC and shown in the session's time zone; it is read as a plain timestamp.");
                return (PortableType.Timestamp, null, First(), null);
            case "timestamp" or "datetime" or "datetime2" or "smalldatetime" or "timestamp_s" or "timestamp_ms" or "timestamp_ns":
                return (withTimeZone ? PortableType.TimestampTz : PortableType.Timestamp, null, First(), null);
            case "timestamptz" or "datetimeoffset":
                return (PortableType.TimestampTz, null, First(), null);
            case "interval":
                return (PortableType.Interval, null, null, null);
            case "uuid" or "uniqueidentifier":
                return (PortableType.Uuid, null, null, null);
            case "json" or "jsonb":
                return (PortableType.Json, null, null, null);
            case "enum" or "set":
                var labels = args == null ? [] : Labels().Matches(args).Select(m => m.Groups[1].Value.Replace("''", "'")).ToList();
                notes.Add($"{spelled} is read as text; the allowed values are not enforced.");
                return (PortableType.VarChar, Math.Max(1, name == "set" ? labels.Sum(l => l.Length + 1) : labels.DefaultIfEmpty("").Max(l => l.Length)), null, null);
        }
        notes.Add($"{spelled} has no equivalent on other engines; it is read as text.");
        return (PortableType.Text, null, null, null);
    }

    /// <summary>
    /// A default every engine can write, or null. SQL Server wraps defaults in parentheses and PostgreSQL casts its
    /// literals ('x'::text); both are taken off. Numbers 0 and 1 in a boolean column are booleans (bit, tinyint(1)).
    /// </summary>
    public static PortableDefault? ReadDefault(string text, PortableType type)
    {
        var value = text.Trim();
        while (value.Length > 1 && value[0] == '(' && value[^1] == ')' && Balanced(value[1..^1])) value = value[1..^1].Trim();
        if (Cast().Match(value) is { Success: true } cast) value = cast.Groups[1].Value.Trim();
        var lower = value.ToLowerInvariant();
        if (lower is "current_timestamp" or "now()" or "getdate()" or "sysdatetime()" or "localtimestamp" or "current_timestamp()" ||
            CurrentTimestampWithPrecision().IsMatch(lower))
            return PortableDefault.CurrentTimestamp;
        if (Quoted().Match(value) is { Success: true } quoted)
            return new(PortableDefaultKind.String, quoted.Groups[1].Value.Replace("''", "'"));
        if (lower is "true" or "false") return new(PortableDefaultKind.Boolean, lower);
        if (Number().IsMatch(value))
            return type == PortableType.Boolean && value is "0" or "1"
                ? new(PortableDefaultKind.Boolean, value == "1" ? "true" : "false")
                : new(PortableDefaultKind.Number, value.TrimStart('+'));
        return null;
    }

    private static bool Balanced(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth < 0) return false;
        }
        return depth == 0;
    }

    [GeneratedRegex(@"^(?<name>[a-z_ ][a-z0-9_ ]*?)\s*(?:\((?<args>.*)\))?\s*(?:\(\d+\))?$", RegexOptions.Singleline)]
    private static partial Regex Spelling();

    [GeneratedRegex(@"'((?:[^']|'')*)'")]
    private static partial Regex Labels();

    [GeneratedRegex(@"^(.*?)::[a-z_ ""\.\[\]0-9(),]+$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Cast();

    [GeneratedRegex(@"^n?'((?:[^']|'')*)'$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^current_timestamp\(\d\)$")]
    private static partial Regex CurrentTimestampWithPrecision();
}
