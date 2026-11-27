using System;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-07 — Classifies a result-set column into a <see cref="ColumnDataType"/> so the grid
/// can pick a culture-invariant converter (and, from RAIN-P2-08 onward, an editor control).
///
/// <see cref="ColumnInfo.ClrType"/> is trusted directly when it's already a specific type
/// (bool/DateTime/decimal/Guid/...). But SQLite reports a column's CLR type from its runtime
/// storage class, not its declared type: a column declared <c>DATE</c>, <c>DATETIME</c>,
/// <c>BOOLEAN</c> or <c>DECIMAL</c> comes back from Microsoft.Data.Sqlite as a plain
/// <see cref="string"/>, <see cref="long"/> or <see cref="double"/> (verified empirically —
/// GetFieldType/GetValue reflect SQLite's storage affinity, not the column's declared type name).
/// So for those four "could just be storage affinity" CLR types, <see cref="ColumnInfo.ProviderTypeName"/>
/// (the driver's declared type name) is checked first and wins when it resolves to something more
/// specific; the ClrType-derived guess is only the fallback. A genuinely-typed provider (Postgres,
/// SQL Server, MySQL, DuckDB) still classifies correctly here, since its declared type name agrees
/// with the CLR-type guess in the ordinary case.
/// </summary>
public static class ColumnTypeResolver
{
    public static ColumnDataType Resolve(ColumnInfo column)
    {
        var fromClrType = ResolveClrType(column.ClrType);
        if (fromClrType is { } specific && !IsAmbiguousStorageAffinityType(column.ClrType))
            return specific;

        return ResolveProviderTypeName(column.ProviderTypeName) ?? fromClrType ?? ColumnDataType.Unknown;
    }

    /// <summary>SQLite's four storage classes (NULL aside) — the CLR types its ClrType/GetValue
    /// report regardless of a column's declared type, so on their own they're not trustworthy proof
    /// of the column's actual semantic type.</summary>
    private static bool IsAmbiguousStorageAffinityType(Type? clrType)
    {
        var type = clrType == null ? null : Nullable.GetUnderlyingType(clrType) ?? clrType;
        return type == typeof(long) || type == typeof(double) || type == typeof(string) || type == typeof(byte[]);
    }

    private static ColumnDataType? ResolveClrType(Type? clrType)
    {
        if (clrType == null) return null;

        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(bool)) return ColumnDataType.Boolean;
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return ColumnDataType.Integer;
        if (type == typeof(float) || type == typeof(double)) return ColumnDataType.Real;
        if (type == typeof(decimal)) return ColumnDataType.Decimal;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return ColumnDataType.DateTime;
        if (type == typeof(Guid)) return ColumnDataType.Guid;
        if (type == typeof(byte[])) return ColumnDataType.Blob;
        if (type == typeof(string)) return ColumnDataType.Text;

        // object (SQLite/DuckDB dynamic columns) or anything else unmapped — let the provider
        // type name have a say instead of guessing.
        return null;
    }

    private static ColumnDataType? ResolveProviderTypeName(string? providerTypeName)
    {
        if (string.IsNullOrWhiteSpace(providerTypeName))
            return null;

        var name = providerTypeName.Trim().ToUpperInvariant();

        // Strip any parenthesized precision/scale, e.g. "DECIMAL(18,4)" -> "DECIMAL".
        var parenIndex = name.IndexOf('(');
        if (parenIndex >= 0)
            name = name[..parenIndex].TrimEnd();

        return name switch
        {
            "BOOL" or "BOOLEAN" or "BIT" => ColumnDataType.Boolean,
            "DATE" => ColumnDataType.Date,
            "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIMESTAMP" or "TIMESTAMPTZ"
                or "TIMESTAMP WITH TIME ZONE" => ColumnDataType.DateTime,
            "GUID" or "UNIQUEIDENTIFIER" or "UUID" => ColumnDataType.Guid,
            "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" or "IMAGE" => ColumnDataType.Blob,
            "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" => ColumnDataType.Decimal,
            "REAL" or "FLOAT" or "FLOAT4" or "FLOAT8" or "DOUBLE" or "DOUBLE PRECISION" => ColumnDataType.Real,
            "INT" or "INTEGER" or "SMALLINT" or "TINYINT" or "BIGINT" or "INT2" or "INT4" or "INT8"
                or "SERIAL" or "BIGSERIAL" or "SMALLSERIAL" => ColumnDataType.Integer,
            "TEXT" or "VARCHAR" or "CHAR" or "CHARACTER" or "NVARCHAR" or "NCHAR" or "CLOB" or "STRING"
                or "VARCHAR2" or "NTEXT" => ColumnDataType.Text,
            "ENUM" => ColumnDataType.Enum,
            "SET" => ColumnDataType.Set,
            "JSON" or "JSONB" => ColumnDataType.Json,
            _ => null
        };
    }
}
