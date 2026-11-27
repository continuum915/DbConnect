using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Reserves a table tab's session across all export pages; other session work waits.</summary>
public sealed class ExportDbSession(IDbSession inner) : IDbSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IDatabaseProvider Provider => inner.Provider;
    public bool IsInTransaction => inner.IsInTransaction;
    public bool IsConnectionLost => inner.IsConnectionLost;

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Func<Task> action, CancellationToken token = default) =>
        RunAsync(async () => { await action(); return true; }, token);

    public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => RunAsync(() => inner.BeginTransactionAsync(cancellationToken), cancellationToken);
    public Task CommitAsync(CancellationToken cancellationToken = default) => RunAsync(() => inner.CommitAsync(cancellationToken), cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken = default) => RunAsync(() => inner.RollbackAsync(cancellationToken), cancellationToken);
    public Task CancelAsync() => inner.CancelAsync();
    public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => RunAsync(() => inner.ExecuteQueryAsync(query, options), options?.CancellationToken ?? default);
    public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
        RunAsync(() => inner.LoadTablePageAsync(tableName, orderByColumns, pageSize, pageIndex, options), options?.CancellationToken ?? default);
    public Task<TablePageResult> LoadTablePageAsync(string tableName, TableQuerySpec spec, QueryExecutionOptions? options = null) =>
        RunAsync(() => inner.LoadTablePageAsync(tableName, spec, options), options?.CancellationToken ?? default);
    public Task<long?> CountTableRowsAsync(string tableName, TableQuerySpec spec, QueryExecutionOptions? options = null) =>
        RunAsync(() => inner.CountTableRowsAsync(tableName, spec, options), options?.CancellationToken ?? default);
    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => RunAsync(() => inner.ExecuteTransactionAsync(commands));

    public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query, QueryExecutionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var result in inner.ExecuteQueryMultipleAsync(query, options, cancellationToken)) yield return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads the table page by page: by OFFSET, or with <paramref name="seek"/> past the last row's key. A page that
    /// ends on a key that cannot be sought is followed by OFFSET past the last key that could. Pages leave out the driver's column schema,
    /// which only names are read from and which Microsoft.Data.Sqlite builds by scanning the table once per column.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadTableAsync(string table, TableQuerySpec captured,
        IReadOnlyList<ExportColumn> columns, TableKeySeek? seek = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // The last key sought past, and the full pages read since it, which OFFSET then skips.
            string? after = null;
            var pagesSince = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spec = captured with
                {
                    PageIndex = pagesSince,
                    RawWhere = after == null ? captured.RawWhere : And(captured.RawWhere, after)
                };
                var page = await inner.LoadTablePageAsync(table, spec, new QueryExecutionOptions
                {
                    CancellationToken = cancellationToken,
                    MaxResultRows = checked(captured.PageSize + 1),
                    IncludeColumnSchema = false
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Result.HasError || page.Result.IsTruncated) throw new InvalidOperationException("Table export page failed or was truncated.");
                var ordinals = Ordinals(page.Result, columns.Select(c => c.Name).ToList(), "Table export columns changed.");
                foreach (var row in page.Result.ObjectRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new object?[columns.Count];
                    for (var i = 0; i < columns.Count; i++)
                    {
                        if (ordinals[i] >= row.Length) throw new InvalidOperationException("Incomplete table export row.");
                        values[i] = row[ordinals[i]];
                    }
                    yield return values;
                }
                if (!page.HasNextPage) yield break;
                if (page.Result.ObjectRows.Count == 0) throw new InvalidOperationException("Table export did not advance.");
                var last = page.Result.ObjectRows[^1];
                var next = seek?.After(Ordinals(page.Result, seek.Columns, "Table export key columns are missing.").Select(o => last[o]).ToList());
                if (next != null) (after, pagesSince) = (next, 0);
                else pagesSince = checked(pagesSince + 1);
            }
        }
        finally { _gate.Release(); }
    }

    private static int[] Ordinals(QueryResult result, IReadOnlyList<string> names, string missing)
    {
        var ordinals = new int[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            ordinals[i] = result.IndexOfColumn(names[i]);
            if (ordinals[i] < 0) throw new InvalidOperationException(missing);
        }
        return ordinals;
    }

    private static string And(string? where, string predicate) =>
        string.IsNullOrWhiteSpace(where) ? predicate : $"({where.Trim()}\n) AND ({predicate})";

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
