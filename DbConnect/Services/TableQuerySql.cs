using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Renders a <see cref="TableQuerySpec"/> for providers that share ANSI-style paging (LIMIT/OFFSET).</summary>
public static class TableQuerySql
{
    public static TablePageQuery BuildLimitOffsetPage(
        DatabaseProviderCapabilities capabilities,
        string tableName,
        TableQuerySpec spec)
    {
        var parameters = new List<object?>();
        var table = QuoteTable(capabilities, tableName, spec.Object);
        var where = BuildWhere(capabilities, spec, parameters);
        var order = capabilities.BuildOrderByClause(spec.Sort);
        var offset = Math.Max(spec.PageIndex, 0) * spec.PageSize;
        var sql = $"SELECT * FROM {table}{where}{order} LIMIT {spec.PageSize + 1} OFFSET {offset};";
        return new TablePageQuery(sql, parameters);
    }

    /// <summary>Renders a page using OFFSET/FETCH paging (SQL Server). OFFSET/FETCH requires an ORDER BY, so an empty sort falls back to a no-op ordering.</summary>
    public static TablePageQuery BuildOffsetFetchPage(
        DatabaseProviderCapabilities capabilities,
        string tableName,
        TableQuerySpec spec)
    {
        var parameters = new List<object?>();
        var table = QuoteTable(capabilities, tableName, spec.Object);
        var where = BuildWhere(capabilities, spec, parameters);
        var order = spec.Sort.Count == 0 ? " ORDER BY (SELECT 1)" : capabilities.BuildOrderByClause(spec.Sort);
        var offset = Math.Max(spec.PageIndex, 0) * spec.PageSize;
        var sql = $"SELECT * FROM {table}{where}{order} OFFSET {offset} ROWS FETCH NEXT {spec.PageSize + 1} ROWS ONLY;";
        return new TablePageQuery(sql, parameters);
    }

    /// <summary>Renders "SELECT COUNT(*) FROM table WHERE ..." honoring the spec's filters; sort and paging do not affect a row count.</summary>
    public static TablePageQuery BuildCountQuery(
        DatabaseProviderCapabilities capabilities,
        string tableName,
        TableQuerySpec spec)
    {
        var parameters = new List<object?>();
        var table = QuoteTable(capabilities, tableName, spec.Object);
        var where = BuildWhere(capabilities, spec, parameters);
        var sql = $"SELECT COUNT(*) FROM {table}{where};";
        return new TablePageQuery(sql, parameters);
    }

    private static string QuoteTable(DatabaseProviderCapabilities capabilities, string tableName, DatabaseObjectIdentity? identity) =>
        identity is null
            ? string.Join(".", tableName.Split('.').Select(capabilities.QuoteIdentifier))
            : ObjectNames.Quote(capabilities, identity);

    private static string BuildWhere(
        DatabaseProviderCapabilities capabilities,
        TableQuerySpec spec,
        List<object?> parameters)
    {
        var predicates = new List<string>();

        // Parenthesised so an OR inside the fragment cannot escape the AND; the newline keeps a
        // trailing "--" comment in the fragment from swallowing the closing parenthesis.
        if (!string.IsNullOrWhiteSpace(spec.RawWhere))
            predicates.Add($"({spec.RawWhere.Trim()}\n)");

        foreach (var filter in spec.Filters)
        {
            var column = capabilities.QuoteIdentifier(filter.Column);
            if (!filter.NeedsValue)
            {
                predicates.Add($"{column} {(filter.Operator == TableFilterOperator.IsNull ? "IS NULL" : "IS NOT NULL")}");
                continue;
            }

            var value = filter.Value ?? string.Empty;
            if (filter.Operator is TableFilterOperator.Contains or TableFilterOperator.StartsWith or TableFilterOperator.EndsWith)
            {
                var escaped = value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                parameters.Add(filter.Operator switch
                {
                    TableFilterOperator.StartsWith => escaped + "%",
                    TableFilterOperator.EndsWith => "%" + escaped,
                    _ => "%" + escaped + "%"
                });
                predicates.Add($"CAST({column} AS {capabilities.TextCastType}) {capabilities.LikeOperator} {capabilities.ParameterPlaceholder(parameters.Count - 1)} ESCAPE '!'");
            }
            else
            {
                parameters.Add(filter.ParameterValue ?? value);
                predicates.Add($"{column} {ComparisonOperator(filter.Operator)} {capabilities.ParameterPlaceholder(parameters.Count - 1)}");
            }
        }

        return predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);
    }

    private static string ComparisonOperator(TableFilterOperator op) => op switch
    {
        TableFilterOperator.Equals => "=",
        TableFilterOperator.NotEquals => "<>",
        TableFilterOperator.GreaterThan => ">",
        TableFilterOperator.GreaterOrEqual => ">=",
        TableFilterOperator.LessThan => "<",
        TableFilterOperator.LessOrEqual => "<=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };
}
