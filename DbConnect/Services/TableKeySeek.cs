using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Pages a table read by its key instead of by OFFSET: each page after the first asks for the rows whose key sorts
/// after the last row read. Every page then costs the same however deep the read is, and a row written or deleted
/// between pages cannot shift the rows that were there across a page boundary, so none is skipped or read twice.
/// The key must be unique, and the read sorts by it ascending. A NULL key value (SQLite allows
/// them, and sorts them first) cannot be sought past, so a page ending on one goes on by OFFSET.
/// </summary>
public sealed class TableKeySeek
{
    private readonly DatabaseProviderCapabilities _dialect;
    private readonly IReadOnlyList<TableDesignColumn> _key;

    private TableKeySeek(DatabaseProviderCapabilities dialect, IReadOnlyList<TableDesignColumn> key)
    {
        _dialect = dialect;
        _key = key;
    }

    /// <summary>A seek over <paramref name="key"/>, or null when it is empty or a column's type cannot be sought (<see cref="DatabaseProviderCapabilities.NonSeekableTypeNames"/>).</summary>
    public static TableKeySeek? For(DatabaseProviderCapabilities dialect, IReadOnlyList<TableDesignColumn> key) =>
        key.Count > 0 && key.All(k => dialect.CanSeekType(k.DataType)) ? new TableKeySeek(dialect, key) : null;

    public IReadOnlyList<string> Columns => _key.Select(k => k.Name).ToList();

    /// <summary>The order the read must sort by: the key, ascending.</summary>
    public IReadOnlyList<TableSortKey> Sort => _key.Select(k => new TableSortKey(k.Name)).ToList();

    /// <summary>
    /// The WHERE fragment for the rows after <paramref name="last"/> (the key values in <see cref="Columns"/> order),
    /// as <c>k1 &gt;= v1 AND (k1 &gt; v1 OR (k1 = v1 AND k2 &gt; v2))</c>, which every engine accepts and can answer
    /// from the key's index. Null when a value is NULL or has no exact literal (a binary floating-point value compared
    /// with a decimal literal is not the value read), so the read must go on by OFFSET.
    /// </summary>
    public string? After(IReadOnlyList<object?> last)
    {
        var literals = new string[_key.Count];
        for (var i = 0; i < _key.Count; i++)
        {
            if (last[i] is null or DBNull or float or double || !SqlLiteral.TryFormat(last[i], _dialect, out var literal)) return null;
            literals[i] = Typed(literal, last[i]!, _key[i]);
        }
        var columns = _key.Select(k => _dialect.QuoteIdentifier(k.Name)).ToArray();
        var predicate = $"{columns[^1]} > {literals[^1]}";
        for (var i = _key.Count - 2; i >= 0; i--)
            predicate = $"{columns[i]} > {literals[i]} OR ({columns[i]} = {literals[i]} AND ({predicate}))";
        return _key.Count == 1 ? predicate : $"{columns[0]} >= {literals[0]} AND ({predicate})";
    }

    /// <summary>
    /// A national (N'…') literal compared with a narrow-string column widens the column instead
    /// (<see cref="DatabaseProviderCapabilities.NarrowStringTypeNames"/>), so it is cast to the column's type. The cast
    /// narrows by the code page of the literal's collation, so the literal takes the column's collation when it has one.
    /// </summary>
    private string Typed(string literal, object value, TableDesignColumn column) =>
        value is string && _dialect.StringLiteralStyle == StringLiteralStyle.National && _dialect.IsNarrowStringType(column.DataType)
            ? $"CAST({literal}{(column.Collation is { Length: > 0 } collation ? $" COLLATE {collation}" : "")} AS {column.DataType})"
            : literal;
}
