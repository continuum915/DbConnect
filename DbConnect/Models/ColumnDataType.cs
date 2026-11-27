namespace DbConnect.Models;

/// <summary>
/// A coarse classification of a column's storage type, used to pick a culture-invariant
/// parser/formatter and (RAIN-P2-07 onward) an editor control. Deliberately provider-agnostic:
/// SQLite/DuckDB report loose or absent CLR types, so <c>ColumnTypeResolver</c> falls back to
/// the provider's declared type name to classify a column.
/// </summary>
public enum ColumnDataType
{
    Text,
    Integer,
    Real,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Guid,
    Blob,
    /// <summary>A single-valued ENUM column (MySQL <c>ENUM(...)</c>, PostgreSQL native enum types).</summary>
    Enum,
    /// <summary>A multi-valued MySQL <c>SET(...)</c> column — a row holds zero or more members at once.</summary>
    Set,
    /// <summary>A JSON/JSONB column; text is validated as JSON before it can be saved.</summary>
    Json,
    Unknown
}
