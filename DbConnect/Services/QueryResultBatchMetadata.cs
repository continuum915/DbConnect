using System;
using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class QueryResultBatchMetadata
{
    public static void Apply(IReadOnlyList<QueryResult> results, string sql, TimeSpan duration,
        IReadOnlyCollection<string> notices)
    {
        foreach (var result in results)
        {
            result.ExecutedSql = sql;
            result.Duration = duration;
        }
        if (results.Count > 0)
            results[^1].Messages.AddRange(notices);
    }

    /// <summary>The results read before a batch failed, followed by its error.</summary>
    public static List<QueryResult> Failed(List<QueryResult> completed, string sql, QueryErrorInfo error, TimeSpan duration,
        List<string> notices)
    {
        Apply(completed, sql, duration, []);
        completed.Add(Error(sql, error, duration, notices));
        return completed;
    }

    public static QueryResult Error(string sql, QueryErrorInfo error, TimeSpan duration,
        List<string> notices) => new()
        {
            ExecutedSql = sql,
            Error = error,
            Duration = duration,
            Messages = notices
        };
}
