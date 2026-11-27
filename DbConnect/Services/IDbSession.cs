using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// One database connection owned by one tab. It carries transaction state and
/// session-scoped objects (temp tables, SET options) between runs and is
/// isolated from every other session, even on the same connection.
/// </summary>
public interface IDbSession : IAsyncDisposable
{
    IDatabaseProvider Provider { get; }

    /// <summary>True between <see cref="BeginTransactionAsync"/> and commit/rollback.</summary>
    bool IsInTransaction { get; }

    /// <summary>True when the server or network closed the connection underneath the session.</summary>
    bool IsConnectionLost => false;

    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels the run in flight, if any. The session stays usable afterwards.</summary>
    Task CancelAsync();

    Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null);

    /// <summary>Loads one page of table rows on this session's connection.</summary>
    Task<TablePageResult> LoadTablePageAsync(
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null);

    /// <summary>Loads one page described by a <see cref="TableQuerySpec"/> on this session's connection.</summary>
    async Task<TablePageResult> LoadTablePageAsync(
        string tableName,
        TableQuerySpec spec,
        QueryExecutionOptions? options = null)
    {
        var query = Provider.BuildTablePageQuery(tableName, spec);
        var result = await ExecuteQueryAsync(query.Sql, (options ?? new QueryExecutionOptions()) with { Parameters = query.Parameters });
        return TablePageResult.FromQuery(result, spec.PageIndex, spec.PageSize);
    }

    /// <summary>Returns the exact row count for the spec's filters on this session's connection, or null when the count could not be read.</summary>
    async Task<long?> CountTableRowsAsync(
        string tableName,
        TableQuerySpec spec,
        QueryExecutionOptions? options = null)
    {
        var query = Provider.BuildTableCountQuery(tableName, spec);
        var result = await ExecuteQueryAsync(query.Sql, (options ?? new QueryExecutionOptions()) with { Parameters = query.Parameters });
        return result.HasError || result.ObjectRows.Count == 0 ? null : Convert.ToInt64(result.ObjectRows[0][0]);
    }

    /// <summary>
    /// Runs parameterized DML commands on this session's connection. When the session has no open
    /// transaction the commands run in their own and roll back together on any failure or count
    /// mismatch; inside an open transaction they enlist in it and leave ending it to the caller.
    /// A successful enlisted result is Pending, with no confirmed committed row count.
    /// Rollback evidence covers supported transactional effects, not nontransactional tables or external side effects.
    /// </summary>
    Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands);

    IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
        string query,
        QueryExecutionOptions? options = null,
        CancellationToken cancellationToken = default);
}
