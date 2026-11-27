using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public static class ResultTabSelection
{
    public const int Limit = 100;

    /// <summary>
    /// Chooses the results that get a tab, since tab headers are not virtualized. A larger batch keeps
    /// results with rows or errors and the final result, which together include whichever one is displayed.
    /// </summary>
    public static IReadOnlyList<QueryResult> Select(IReadOnlyList<QueryResult> results)
    {
        if (results.Count <= Limit)
            return results;

        var selected = new List<QueryResult>();
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.HasResultSet || result.HasError || i == results.Count - 1)
                selected.Add(result);
        }

        return selected.Count <= Limit ? selected : selected.TakeLast(Limit).ToList();
    }
}
