using System;

namespace DbConnect.Models;

/// <summary>
/// Describes one column of a result set by its reader ordinal. Two columns with the
/// same <see cref="Name"/> (e.g. <c>SELECT a.id, b.id</c>) are distinct entries here,
/// told apart by <see cref="Ordinal"/> rather than collapsed by name.
/// </summary>
public sealed class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    /// <summary>The driver's own type name (e.g. "varchar", "int4"), when the reader exposes one.</summary>
    public string? ProviderTypeName { get; set; }
    public Type? ClrType { get; set; }
    /// <summary>The schema of <see cref="SourceTable"/>, when the driver reports one (null without a source table).</summary>
    public string? SourceSchema { get; set; }
    /// <summary>The underlying table this column comes from, when the driver can resolve it (e.g. not for an expression).</summary>
    public string? SourceTable { get; set; }
    /// <summary>The underlying column this comes from, when the driver can resolve it.</summary>
    public string? SourceColumn { get; set; }
    /// <summary>Null when the driver doesn't know (common for expression/computed columns), not false.</summary>
    public bool? IsNullable { get; set; }
    /// <summary>Null when the driver doesn't know, not false.</summary>
    public bool? IsKey { get; set; }
}
