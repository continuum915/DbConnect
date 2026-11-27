using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A column type as one engine writes it, with a note when it cannot hold everything the portable type can.</summary>
public sealed record PortableTypeRendering(string Type, string? Note = null);

/// <summary>How one engine writes portable columns: their types, identity and the current time as a default.</summary>
public interface IPortableTypeDialect
{
    /// <param name="inKey">The column is part of the primary key, where some engines refuse unbounded types.</param>
    PortableTypeRendering Type(PortableColumn column, bool inKey);

    /// <summary>The clause that makes the column generate its values, or null with <paramref name="note"/> saying why not.</summary>
    string? Identity(PortableColumn column, string renderedType, bool soleKey, bool inKey, out string? note);

    /// <summary>The current time as a default for a column of <paramref name="renderedType"/>.</summary>
    string CurrentTimestamp(string renderedType) => "CURRENT_TIMESTAMP";

    /// <summary>Wraps a literal default where the engine needs it (MySQL: text, blob and JSON defaults are expressions).</summary>
    string Default(string literal, string renderedType) => literal;

    /// <summary>The schema a table goes to when the connection names none; null where the engine has no schemas (MySQL).</summary>
    string? DefaultSchema { get; }

    /// <summary>Foreign keys can be added with ALTER TABLE after the rows; otherwise they are written into CREATE TABLE.</summary>
    bool AddsForeignKeysAfterCreate => true;

    /// <summary>CREATE INDEX names its table without a schema (SQLite qualifies the index name instead).</summary>
    bool IndexesNameBareTable => false;

    /// <summary>The ON DELETE / ON UPDATE clauses, leaving out what the engine does not support with <paramref name="note"/>.</summary>
    string ForeignKeyActions(string onDelete, string onUpdate, out string? note)
    {
        note = null;
        return PortableTypeDialects.Action("DELETE", onDelete) + PortableTypeDialects.Action("UPDATE", onUpdate);
    }
}

/// <summary>Portable type dialects by provider, mirroring <see cref="TableMaintenanceDialects"/>.</summary>
public static class PortableTypeDialects
{
    private static readonly Dictionary<string, IPortableTypeDialect> Dialects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLite"] = new Sqlite(),
        ["PostgreSQL"] = new PostgreSql(),
        ["SQL Server"] = new SqlServer(),
        ["MySQL"] = new MySql(),
        ["DuckDB"] = new DuckDb()
    };

    public static IPortableTypeDialect? For(string providerName) => Dialects.GetValueOrDefault(providerName);

    /// <summary>A foreign key's action as SQL words ("SET NULL"), whatever spelling the design reports.</summary>
    internal static string Normalize(string action) => action.Replace('_', ' ').Trim().ToUpperInvariant() switch
    {
        "NOACTION" or "" => "NO ACTION",
        "SETNULL" => "SET NULL",
        "SETDEFAULT" => "SET DEFAULT",
        var words => words
    };

    internal static string Action(string verb, string action) =>
        Normalize(action) is "NO ACTION" ? "" : $" ON {verb} {Normalize(action)}";

    /// <summary>The actions an engine supports; any other is left out (NO ACTION) and named in <paramref name="note"/>.</summary>
    private static string Supported(string onDelete, string onUpdate, Func<string, bool> supported, string engine, out string? note)
    {
        var dropped = new[] { ("DELETE", onDelete), ("UPDATE", onUpdate) }
            .Where(a => Normalize(a.Item2) != "NO ACTION" && !supported(Normalize(a.Item2))).ToList();
        note = dropped.Count == 0 ? null
            : $"{engine} does not support {string.Join(" or ", dropped.Select(a => $"ON {a.Item1} {Normalize(a.Item2)}"))}; it is left out.";
        return string.Concat(new[] { ("DELETE", onDelete), ("UPDATE", onUpdate) }
            .Where(a => supported(Normalize(a.Item2))).Select(a => Action(a.Item1, a.Item2)));
    }

    private static bool IsInteger(PortableType type) => type is PortableType.Int8 or PortableType.Int16 or PortableType.Int32 or PortableType.Int64;

    private static string Decimal(PortableColumn c, int maxPrecision, int maxScale, string name, out string? note)
    {
        note = null;
        if (c.Precision is not { } precision)
        {
            note = $"Unconstrained {c.SourceType} becomes {name}({maxPrecision},{Math.Min(10, maxScale)}).";
            return $"{name}({maxPrecision},{Math.Min(10, maxScale)})";
        }
        if (precision <= maxPrecision && (c.Scale ?? 0) <= maxScale) return $"{name}({precision},{c.Scale ?? 0})";
        note = $"{c.SourceType} is wider than {name} allows; it becomes {name}({maxPrecision},{Math.Min(c.Scale ?? 0, maxScale)}).";
        return $"{name}({maxPrecision},{Math.Min(c.Scale ?? 0, maxScale)})";
    }

    private sealed class Sqlite : IPortableTypeDialect
    {
        public PortableTypeRendering Type(PortableColumn c, bool inKey) => c.Type switch
        {
            PortableType.Boolean => new("BOOLEAN", "SQLite stores booleans as 0 and 1."),
            PortableType.Int8 or PortableType.Int16 or PortableType.Int32 or PortableType.Int64 => new("INTEGER"),
            PortableType.Decimal => new(c.Precision is { } p ? $"NUMERIC({p},{c.Scale ?? 0})" : "NUMERIC",
                "SQLite stores decimals as integers or 8-byte floats, so digits beyond 15 can be lost."),
            PortableType.Single or PortableType.Double => new("REAL"),
            PortableType.Char => new($"CHAR({c.Length})", "SQLite does not enforce the length."),
            PortableType.VarChar => new($"VARCHAR({c.Length})", "SQLite does not enforce the length."),
            PortableType.Text => new("TEXT"),
            PortableType.Binary or PortableType.VarBinary or PortableType.Blob => new("BLOB"),
            PortableType.Date => new("DATE", "SQLite stores dates as text."),
            PortableType.Time => new("TIME", "SQLite stores times as text."),
            PortableType.Timestamp => new("DATETIME", "SQLite stores timestamps as text."),
            PortableType.TimestampTz => new("DATETIME", "SQLite stores instants as text with their offset."),
            PortableType.Interval => new("TEXT", "SQLite has no interval type; intervals are stored as text."),
            PortableType.Uuid => new("TEXT", "SQLite has no uuid type; uuids are stored as text."),
            _ => new("TEXT", "SQLite stores JSON as text.")
        };

        public string? Identity(PortableColumn c, string renderedType, bool soleKey, bool inKey, out string? note)
        {
            // Only INTEGER PRIMARY KEY generates values in SQLite; the key is then written on the column.
            note = soleKey && IsInteger(c.Type) ? null : $"{c.Name}: SQLite generates values only for a single INTEGER PRIMARY KEY; the values are copied, but no new ones are generated.";
            return note == null ? "PRIMARY KEY" : null;
        }

        public string? DefaultSchema => "main";
        public bool AddsForeignKeysAfterCreate => false;
        public bool IndexesNameBareTable => true;
    }

    private sealed class PostgreSql : IPortableTypeDialect
    {
        public PortableTypeRendering Type(PortableColumn c, bool inKey) => c.Type switch
        {
            PortableType.Boolean => new("boolean"),
            PortableType.Int8 or PortableType.Int16 => new("smallint"),
            PortableType.Int32 => new("integer"),
            PortableType.Int64 => new("bigint"),
            PortableType.Decimal => new(c.Precision is { } p ? $"numeric({Math.Min(p, 1000)},{c.Scale ?? 0})" : "numeric"),
            PortableType.Single => new("real"),
            PortableType.Double => new("double precision"),
            PortableType.Char => new($"char({c.Length})"),
            PortableType.VarChar => new($"varchar({c.Length})"),
            PortableType.Text => new("text"),
            PortableType.Binary or PortableType.VarBinary or PortableType.Blob => new("bytea"),
            PortableType.Date => new("date"),
            PortableType.Time => new(c.Precision is { } p ? $"time({Math.Min(p, 6)})" : "time"),
            PortableType.Timestamp => new(c.Precision is { } p ? $"timestamp({Math.Min(p, 6)})" : "timestamp"),
            PortableType.TimestampTz => new(c.Precision is { } p ? $"timestamptz({Math.Min(p, 6)})" : "timestamptz"),
            PortableType.Interval => new("interval"),
            PortableType.Uuid => new("uuid"),
            _ => new("jsonb")
        };

        public string? Identity(PortableColumn c, string renderedType, bool soleKey, bool inKey, out string? note)
        {
            note = IsInteger(c.Type) ? null : $"{c.Name}: PostgreSQL identity columns must be integers; the values are copied, but no new ones are generated.";
            return note == null ? "GENERATED BY DEFAULT AS IDENTITY" : null;
        }

        public string? DefaultSchema => "public";
    }

    private sealed class SqlServer : IPortableTypeDialect
    {
        public PortableTypeRendering Type(PortableColumn c, bool inKey)
        {
            switch (c.Type)
            {
                case PortableType.Boolean: return new("bit");
                case PortableType.Int8 or PortableType.Int16: return new("smallint");
                case PortableType.Int32: return new("int");
                case PortableType.Int64: return new("bigint");
                case PortableType.Decimal:
                    var type = Decimal(c, 38, 38, "decimal", out var note);
                    return new(type, note);
                case PortableType.Single: return new("real");
                case PortableType.Double: return new("float");
                case PortableType.Char: return c.Length <= 4000 ? new($"nchar({c.Length})") : new("nvarchar(max)", $"{c.SourceType} is longer than nchar allows.");
                case PortableType.VarChar: return c.Length <= 4000 ? new($"nvarchar({c.Length})") : new("nvarchar(max)");
                case PortableType.Text:
                    return inKey ? new("nvarchar(450)", $"{c.Name} is part of the primary key, whose columns SQL Server limits to 900 bytes; it becomes nvarchar(450).")
                        : new("nvarchar(max)");
                case PortableType.Binary: return c.Length <= 8000 ? new($"binary({c.Length})") : new("varbinary(max)");
                case PortableType.VarBinary: return c.Length <= 8000 ? new($"varbinary({c.Length})") : new("varbinary(max)");
                case PortableType.Blob:
                    return inKey ? new("varbinary(900)", $"{c.Name} is part of the primary key, whose columns SQL Server limits to 900 bytes.")
                        : new("varbinary(max)");
                case PortableType.Date: return new("date");
                case PortableType.Time: return new($"time({Math.Min(c.Precision ?? 7, 7)})");
                case PortableType.Timestamp: return new($"datetime2({Math.Min(c.Precision ?? 7, 7)})");
                case PortableType.TimestampTz: return new($"datetimeoffset({Math.Min(c.Precision ?? 7, 7)})");
                case PortableType.Interval: return new("nvarchar(100)", "SQL Server has no interval type; intervals are stored as text.");
                case PortableType.Uuid: return new("uniqueidentifier");
                default: return new("nvarchar(max)", "SQL Server stores JSON as text.");
            }
        }

        public string? Identity(PortableColumn c, string renderedType, bool soleKey, bool inKey, out string? note)
        {
            note = IsInteger(c.Type) ? null : $"{c.Name}: SQL Server identity columns must be integers; the values are copied, but no new ones are generated.";
            return note == null ? "IDENTITY(1,1)" : null;
        }

        public string CurrentTimestamp(string renderedType) => "SYSDATETIME()";

        public string? DefaultSchema => "dbo";

        public string ForeignKeyActions(string onDelete, string onUpdate, out string? note) =>
            Supported(onDelete, onUpdate, a => a is not "RESTRICT", "SQL Server", out note);
    }

    private sealed class MySql : IPortableTypeDialect
    {
        public PortableTypeRendering Type(PortableColumn c, bool inKey)
        {
            switch (c.Type)
            {
                case PortableType.Boolean: return new("tinyint(1)", "MySQL stores booleans as 0 and 1.");
                case PortableType.Int8: return new("tinyint");
                case PortableType.Int16: return new("smallint");
                case PortableType.Int32: return new("int");
                case PortableType.Int64: return new("bigint");
                case PortableType.Decimal:
                    var type = Decimal(c, 65, 30, "decimal", out var note);
                    return new(type, note);
                case PortableType.Single: return new("float");
                case PortableType.Double: return new("double");
                case PortableType.Char: return c.Length <= 255 ? new($"char({c.Length})") : new($"varchar({c.Length})");
                case PortableType.VarChar:
                    return c.Length <= 16383 ? new($"varchar({c.Length})") : inKey ? new("varchar(768)", KeyNote(c)) : new("longtext");
                case PortableType.Text: return inKey ? new("varchar(768)", KeyNote(c)) : new("longtext");
                case PortableType.Binary: return c.Length <= 255 ? new($"binary({c.Length})") : new($"varbinary({c.Length})");
                case PortableType.VarBinary: return c.Length <= 65535 ? new($"varbinary({c.Length})") : new("longblob");
                case PortableType.Blob: return inKey ? new("varbinary(3072)", KeyNote(c)) : new("longblob");
                case PortableType.Date: return new("date");
                case PortableType.Time: return new($"time({Math.Min(c.Precision ?? 6, 6)})");
                case PortableType.Timestamp: return new($"datetime({Math.Min(c.Precision ?? 6, 6)})");
                case PortableType.TimestampTz:
                    return new($"datetime({Math.Min(c.Precision ?? 6, 6)})", "MySQL has no type that keeps an offset; instants are stored as UTC timestamps.");
                case PortableType.Interval: return new("varchar(100)", "MySQL has no interval type; intervals are stored as text.");
                case PortableType.Uuid: return new("char(36)", "MySQL has no uuid type; uuids are stored as text.");
                default: return new("json");
            }
        }

        private static string KeyNote(PortableColumn c) =>
            $"{c.Name} is part of the primary key, and MySQL keys cannot hold unbounded text or bytes; it is limited to 3,072 bytes.";

        public string? Identity(PortableColumn c, string renderedType, bool soleKey, bool inKey, out string? note)
        {
            note = !IsInteger(c.Type) ? $"{c.Name}: MySQL AUTO_INCREMENT columns must be integers; the values are copied, but no new ones are generated."
                : !inKey ? $"{c.Name}: MySQL AUTO_INCREMENT columns must be keys; the values are copied, but no new ones are generated."
                : null;
            return note == null ? "AUTO_INCREMENT" : null;
        }

        public string CurrentTimestamp(string renderedType) =>
            renderedType.StartsWith("datetime(", StringComparison.Ordinal) ? "CURRENT_TIMESTAMP" + renderedType["datetime".Length..] : "CURRENT_TIMESTAMP";

        public string Default(string literal, string renderedType) =>
            renderedType is "longtext" or "longblob" or "json" ? $"({literal})" : literal;

        public string? DefaultSchema => null;

        public string ForeignKeyActions(string onDelete, string onUpdate, out string? note) =>
            Supported(onDelete, onUpdate, a => a is not "SET DEFAULT", "MySQL", out note);
    }

    private sealed class DuckDb : IPortableTypeDialect
    {
        public PortableTypeRendering Type(PortableColumn c, bool inKey) => c.Type switch
        {
            PortableType.Boolean => new("BOOLEAN"),
            PortableType.Int8 => new("TINYINT"),
            PortableType.Int16 => new("SMALLINT"),
            PortableType.Int32 => new("INTEGER"),
            PortableType.Int64 => new("BIGINT"),
            PortableType.Decimal => Decimal(c),
            PortableType.Single => new("REAL"),
            PortableType.Double => new("DOUBLE"),
            PortableType.Char or PortableType.VarChar => new("VARCHAR", "DuckDB does not keep text lengths."),
            PortableType.Text => new("VARCHAR"),
            PortableType.Binary or PortableType.VarBinary or PortableType.Blob => new("BLOB"),
            PortableType.Date => new("DATE"),
            PortableType.Time => new("TIME"),
            PortableType.Timestamp => new("TIMESTAMP"),
            PortableType.TimestampTz => new("TIMESTAMPTZ"),
            PortableType.Interval => new("INTERVAL"),
            PortableType.Uuid => new("UUID"),
            _ => new("JSON")
        };

        private static PortableTypeRendering Decimal(PortableColumn c)
        {
            var type = PortableTypeDialects.Decimal(c, 38, 38, "DECIMAL", out var note);
            return new(type, note);
        }

        public string? Identity(PortableColumn c, string renderedType, bool soleKey, bool inKey, out string? note)
        {
            note = $"{c.Name}: DuckDB has no identity columns; the values are copied, but no new ones are generated.";
            return null;
        }

        public string? DefaultSchema => "main";
        public bool AddsForeignKeysAfterCreate => false;

        public string ForeignKeyActions(string onDelete, string onUpdate, out string? note) =>
            Supported(onDelete, onUpdate, a => a is "RESTRICT", "DuckDB", out note);
    }
}
