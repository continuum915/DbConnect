using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public enum StopCleanupFault { None, Hang, Throw }

/// <summary>A small database boundary for stop-behavior checks; never opens a connection.</summary>
public sealed class StopDbSession : IDbSession
{
    private readonly bool _blockSecondCommand;
    private readonly StopCleanupFault _cleanupFault;
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public StopDbSession(bool blockSecondCommand = false, StopCleanupFault cleanupFault = StopCleanupFault.None)
    {
        _blockSecondCommand = blockSecondCommand;
        _cleanupFault = cleanupFault;
    }
    public Task Blocked => _blocked.Task;
    public void ReleaseBlockedCommand() => _release.TrySetResult();
    public const string ErrorCode = "DBCONNECT_FAKE_ERROR";
    public IDatabaseProvider Provider { get; } = new SqlServerProvider();
    public bool IsInTransaction => false;
    public List<string> SubmittedCommands { get; } = [];
    public int ActiveCommands { get; private set; }
    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query,
        QueryExecutionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        SubmittedCommands.Add(query);
        ActiveCommands++;
        try
        {
            QueryErrorInfo? error = null;
            if (SubmittedCommands.Count == 2)
            {
                if (_blockSecondCommand)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        options?.CancellationToken ?? default, cancellationToken);
                    _blocked.TrySetResult();
                    try { await _release.Task.WaitAsync(linked.Token); }
                    catch (OperationCanceledException) { error = QueryCancellation.CancelledError(Provider.ProviderName); }
                }
                else error = new QueryErrorInfo
                {
                    Provider = Provider.ProviderName,
                    Code = ErrorCode,
                    Summary = "Deterministic command two failure."
                };
            }
            yield return error == null ? new QueryResult { RecordsAffected = 1 } : new QueryResult { Error = error };
        }
        finally { ActiveCommands--; }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseBlockedCommand();
        Disposed = true;
        if (_cleanupFault == StopCleanupFault.Throw) throw new InvalidOperationException("Injected cleanup failure.");
        if (_cleanupFault == StopCleanupFault.Hang) await Task.Delay(Timeout.Infinite);
    }
    public Task CancelAsync() => throw new NotSupportedException();
    public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => throw new NotSupportedException();
    public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns,
        int pageSize, int pageIndex, QueryExecutionOptions? options = null) => throw new NotSupportedException();
    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => throw new NotSupportedException();
}
