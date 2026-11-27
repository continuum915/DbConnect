using System.Linq;

namespace DbConnect.Models;

public sealed class TablePageResult
{
    public QueryResult Result { get; init; } = new();
    public int PageIndex { get; init; }
    public int PageSize { get; init; }
    public bool HasNextPage { get; init; }

    /// <summary>Wraps a page query that fetched one row beyond the page size, trimming it and noting whether more rows exist.</summary>
    public static TablePageResult FromQuery(QueryResult result, int pageIndex, int pageSize)
    {
        var hasNextPage = !result.HasError && result.ObjectRows.Count > pageSize;
        if (hasNextPage)
            result.ObjectRows = result.ObjectRows.Take(pageSize).ToList();

        return new TablePageResult
        {
            Result = result,
            PageIndex = pageIndex,
            PageSize = pageSize,
            HasNextPage = hasNextPage
        };
    }
}
