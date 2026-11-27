using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>The kinds of value a column holds, independent of any engine's spelling of them.</summary>
public enum PortableType
{
    Boolean,
    Int8,
    Int16,
    Int32,
    Int64,
    /// <summary>Exact numeric; <see cref="PortableColumn.Precision"/> null means unconstrained (PostgreSQL numeric).</summary>
    Decimal,
    Single,
    Double,
    /// <summary>Fixed-length text of <see cref="PortableColumn.Length"/> characters.</summary>
    Char,
    /// <summary>Text of at most <see cref="PortableColumn.Length"/> characters.</summary>
    VarChar,
    /// <summary>Text without a declared limit.</summary>
    Text,
    /// <summary>Fixed-length bytes.</summary>
    Binary,
    /// <summary>Bytes up to <see cref="PortableColumn.Length"/>.</summary>
    VarBinary,
    /// <summary>Bytes without a declared limit.</summary>
    Blob,
    Date,
    Time,
    Timestamp,
    /// <summary>An instant: a timestamp with a time zone or an offset.</summary>
    TimestampTz,
    Interval,
    Uuid,
    Json
}

/// <summary>A column default that every engine can write: a literal, or the current time.</summary>
public sealed record PortableDefault(PortableDefaultKind Kind, string? Text = null)
{
    public static PortableDefault CurrentTimestamp { get; } = new(PortableDefaultKind.CurrentTimestamp);
}

public enum PortableDefaultKind
{
    /// <summary>A quoted string; <see cref="PortableDefault.Text"/> holds its value.</summary>
    String,
    /// <summary>A number; <see cref="PortableDefault.Text"/> holds it in invariant notation.</summary>
    Number,
    /// <summary>TRUE or FALSE; <see cref="PortableDefault.Text"/> is "true" or "false".</summary>
    Boolean,
    CurrentTimestamp
}

/// <summary>
/// A column described the same way for every engine, read from one engine's table design so another can create it.
/// <see cref="Notes"/> say what the description could not keep from the source (an enum's labels, an array's element
/// type, a default expression), so a transfer can report it.
/// </summary>
public sealed record PortableColumn(string Name, PortableType Type, bool IsNullable)
{
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public PortableDefault? Default { get; init; }
    /// <summary>The engine generates the column's values (identity, AUTO_INCREMENT, serial, rowid alias).</summary>
    public bool IsIdentity { get; init; }
    /// <summary>The source's collation, reported because engines name collations differently and none is carried over.</summary>
    public string? Collation { get; init; }
    /// <summary>The source engine's own spelling of the type.</summary>
    public string SourceType { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A table's portable columns and primary key.</summary>
public sealed record PortableTable(DatabaseObjectIdentity Table, IReadOnlyList<PortableColumn> Columns, IReadOnlyList<string> PrimaryKey);
