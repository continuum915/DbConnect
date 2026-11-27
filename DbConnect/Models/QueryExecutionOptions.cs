using System.Collections.Generic;
using System.Threading;

namespace DbConnect.Models;

public sealed record QueryExecutionOptions
{
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxResultRows { get; init; } = 1000;
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Positional values for the query's placeholders; providers bind them as parameters.</summary>
    public IReadOnlyList<object?>? Parameters { get; init; }

    /// <summary>Lets a result that reaches the row cap keep its reader open for fetching more rows.</summary>
    public DbConnect.Services.ResultContinuation? Continuation { get; init; }

    /// <summary>
    /// Whether result columns carry the driver's column schema (source table and column, key, nullability). Reads that
    /// need only names and values turn it off: Microsoft.Data.Sqlite builds it by scanning the table once per column.
    /// </summary>
    public bool IncludeColumnSchema { get; init; } = true;
}
