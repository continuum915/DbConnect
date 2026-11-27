using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Database query operations, independent of other provider roles.</summary>
public interface IDatabaseQueryProvider
{
    /// <summary>Human-readable name shown in the UI (e.g. "SQLite", "PostgreSQL").</summary>
    string ProviderName { get; }

    /// <summary>Executes a single SQL statement and returns rows or rows-affected metadata.</summary>
    Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null);

    /// <summary>
    /// Executes SQL and reads every result set it produces natively (via the driver's
    /// result-set traversal), yielding one <see cref="QueryResult"/> per result set.
    /// Every provider implements this itself; there is no shared fallback.
    /// </summary>
    IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
        string connectionString,
        string query,
        QueryExecutionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Builds the query for one page, fetching one row past the page size so the caller can tell whether another page exists.</summary>
    string BuildTablePageQuery(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex) =>
        throw new NotSupportedException($"{ProviderName} does not build table page queries.");

    /// <summary>Loads one page of table rows using provider-native paging syntax.</summary>
    Task<TablePageResult> LoadTablePageAsync(
        string connectionString,
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null);

    /// <summary>Renders the page query and its parameters for a <see cref="TableQuerySpec"/>; only providers with <see cref="DatabaseProviderCapabilities.SupportsTableQuerySpec"/> implement it.</summary>
    TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
        throw new NotSupportedException($"{ProviderName} does not render table query specs.");

    /// <summary>Renders a COUNT(*) query honoring the spec's filters (sort and paging are irrelevant to a row count); only providers with <see cref="DatabaseProviderCapabilities.SupportsTableQuerySpec"/> implement it.</summary>
    TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
        throw new NotSupportedException($"{ProviderName} does not render table count queries.");

    /// <summary>Returns the exact row count for the spec's filters, or null when the count could not be read (e.g. a failed query).</summary>
    async Task<long?> CountTableRowsAsync(
        string connectionString,
        string tableName,
        TableQuerySpec spec,
        QueryExecutionOptions? options = null)
    {
        var query = BuildTableCountQuery(tableName, spec);
        var result = await ExecuteQueryAsync(connectionString, query.Sql, (options ?? new QueryExecutionOptions()) with { Parameters = query.Parameters });
        return result.HasError || result.ObjectRows.Count == 0 ? null : Convert.ToInt64(result.ObjectRows[0][0]);
    }

    /// <summary>Loads one page of table rows described by a <see cref="TableQuerySpec"/>.</summary>
    async Task<TablePageResult> LoadTablePageAsync(
        string connectionString,
        string tableName,
        TableQuerySpec spec,
        QueryExecutionOptions? options = null)
    {
        var query = BuildTablePageQuery(tableName, spec);
        var result = await ExecuteQueryAsync(connectionString, query.Sql, (options ?? new QueryExecutionOptions()) with { Parameters = query.Parameters });
        return TablePageResult.FromQuery(result, spec.PageIndex, spec.PageSize);
    }

    /// <summary>Executes DDL or DML that does not return rows; returns rows-affected count.</summary>
    Task<int> ExecuteNonQueryAsync(string connectionString, string sql);

    /// <summary>Executes DDL or DML statements as one provider-native transaction.</summary>
    Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements);

    /// <summary>
    /// Executes parameterized DML commands as one provider-native transaction.
    /// Commands with <see cref="SqlCommandSpec.ExpectedRecordsAffected"/> must
    /// attempt rollback and return a typed conflict when the exact count is not met.
    /// Inspect the result's Outcome and BoundaryErrorMessage before claiming rollback protection.
    /// Rollback evidence covers supported transactional effects only.
    /// </summary>
    Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands);

    /// <summary>
    /// Wraps a query with the provider's native row-limiting syntax.
    /// e.g. SQLite/PostgreSQL → LIMIT n, SQL Server → SELECT TOP n …
    /// </summary>
    string FormatLimitQuery(string query, int limit);

    /// <summary>
    /// SQL prefix that produces a human-readable execution plan for this provider.
    /// e.g. "EXPLAIN QUERY PLAN" for SQLite, "EXPLAIN" for PostgreSQL.
    /// </summary>
    string ExplainPrefix { get; }
    /// <summary>
    /// Normalizes a raw EXPLAIN result into a display-friendly QueryResult.
    /// </summary>
    QueryResult NormalizeExplainResult(QueryResult raw);
}
