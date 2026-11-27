using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>One column in a table query's ORDER BY.</summary>
public sealed record TableSortKey(string Column, bool Descending = false);

public enum TableFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    IsNull,
    IsNotNull
}

/// <summary>A column predicate. The value is always sent as a parameter, never spliced into SQL.</summary>
public sealed record TableFilter(string Column, TableFilterOperator Operator, string? Value = null)
{
    /// <summary>Optional database value for navigation predicates; preserves binary and temporal keys.</summary>
    public object? ParameterValue { get; init; }

    public bool NeedsValue => Operator is not (TableFilterOperator.IsNull or TableFilterOperator.IsNotNull);
}

/// <summary>What a table-data tab asks a provider to load: filters, an ordered sort list, and a page.</summary>
public sealed record TableQuerySpec(IReadOnlyList<TableSortKey> Sort, int PageIndex, int PageSize)
{
    /// <summary>Selected relation. Null retains the legacy dotted-name adapter.</summary>
    public DatabaseObjectIdentity? Object { get; init; }

    /// <summary>Predicates combined with AND.</summary>
    public IReadOnlyList<TableFilter> Filters { get; init; } = [];

    /// <summary>A user-written WHERE fragment, embedded verbatim and ANDed with <see cref="Filters"/>. Blank means none.</summary>
    public string? RawWhere { get; init; }
}

/// <summary>Rendered SQL plus its positional parameter values, in placeholder order.</summary>
public sealed record TablePageQuery(string Sql, IReadOnlyList<object?> Parameters);
