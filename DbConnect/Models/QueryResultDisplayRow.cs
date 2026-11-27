using System.Collections;
using System.Collections.Generic;
using DbConnect.Services;

namespace DbConnect.Models;

/// <summary>
/// One display-ready result row, indexed by column ordinal rather than name so that
/// duplicate column names (e.g. <c>SELECT a.id, b.id</c>) stay distinct instead of one
/// overwriting the other's value the way a name-keyed dictionary would.
/// </summary>
public sealed class QueryResultDisplayRow
{
    public QueryResultDisplayRow(object?[] rawValues, int columnCount)
    {
        RawValues = rawValues;
        DisplayValues = new LazyDisplayValues(rawValues, columnCount);
    }

    public object?[] RawValues { get; }
    /// <summary>List, not array, so Avalonia's numeric indexer binding (<c>[0]</c>) resolves against it.</summary>
    public IReadOnlyList<string> DisplayValues { get; }

    /// <summary>
    /// The raw values behind a grid item, which is a row's <see cref="DisplayValues"/>; null for a
    /// list that didn't come from a <see cref="QueryResultDisplayRow"/>.
    /// </summary>
    public static IReadOnlyList<object?>? RawValuesOf(IReadOnlyList<string> displayValues) =>
        (displayValues as LazyDisplayValues)?.RawValues;
}

/// <summary>
/// Formats each cell on first access and caches it, rather than formatting every cell of every
/// row up front — a virtualizing grid only ever touches the rows and columns it renders, so this
/// keeps large results responsive to load.
/// </summary>
internal sealed class LazyDisplayValues : IReadOnlyList<string>
{
    private readonly object?[] _rawValues;
    private readonly string?[] _cache;

    public LazyDisplayValues(object?[] rawValues, int columnCount)
    {
        _rawValues = rawValues;
        _cache = new string?[columnCount];
    }

    public int Count => _cache.Length;

    public object?[] RawValues => _rawValues;

    public string this[int index]
    {
        get
        {
            var cached = _cache[index];
            if (cached != null)
                return cached;

            var formatted = QueryResultFormattingService.FormatValue(index < _rawValues.Length ? _rawValues[index] : null);
            _cache[index] = formatted;
            return formatted;
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
