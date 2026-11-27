using System.Diagnostics;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

internal sealed class ImmediateDbSession : IDbSession
{
    internal const ulong InitialHash = 14695981039346656037;
    public IDatabaseProvider Provider { get; } = new SqlServerProvider();
    public bool IsInTransaction => false;
    internal long StartTimestamp { get; set; }
    internal double FirstSubmissionMilliseconds { get; private set; }
    internal int CommandCount { get; private set; }
    internal long Characters { get; private set; }
    internal ulong Hash { get; private set; } = InitialHash;
    internal TimeSpan ReportedStatementDuration { get; set; }
    internal Action<int>? CommandSubmitted { get; set; }

    public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query,
        QueryExecutionOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (CommandCount == 0)
            FirstSubmissionMilliseconds = Stopwatch.GetElapsedTime(StartTimestamp).TotalMilliseconds;
        CommandCount++;
        CommandSubmitted?.Invoke(CommandCount);
        Characters += query.Length;
        Hash = AppendHash(Hash, query);
        yield return new QueryResult { RecordsAffected = 1, Duration = ReportedStatementDuration };
        await Task.CompletedTask;
    }

    internal static ulong AppendHash(ulong hash, string query)
    {
        foreach (var character in query)
            hash = unchecked((hash ^ character) * 1099511628211);
        return unchecked((hash ^ (uint)query.Length) * 1099511628211);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CancelAsync() => throw new NotSupportedException();
    public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => throw new NotSupportedException();
    public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns,
        int pageSize, int pageIndex, QueryExecutionOptions? options = null) => throw new NotSupportedException();
    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => throw new NotSupportedException();
}
