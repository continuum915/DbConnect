using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public sealed class TableExportSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-export-session-{Guid.NewGuid():N}");
    public TableExportSessionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task WritesPagesIncrementallyWhileOtherSessionsRemainUsable()
    {
        var inner = new PagedSession("complete");
        await using var session = new ExportDbSession(inner);
        var path = Path.Combine(_directory, "rows.csv");
        var source = new TableExportSource(_ => Task.FromResult<IDbSession>(session), "Customers", new([new("Id")], 0, 1), [new("Id", 0), new("Name", 1)]);
        var written = new WrittenProgress();
        var operation = new ResultExporter(new Picker(path)).ExportAsync(source, new(), ExportScope.AllMatchingTableRows, progress: written);
        await inner.Fetching.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await written.FirstRow.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(path));
            Assert.True(new FileInfo(Assert.Single(Directory.GetFiles(_directory))).Length > 0);
            var unrelated = new PagedSession("complete");
            await using var other = new ExportDbSession(unrelated);
            await other.ExecuteQueryAsync("other tab").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(unrelated.Queried);
        }
        finally { inner.Continue.SetResult(); await operation; }
        var outcome = await operation;
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal(2, outcome.RowsWritten);
        Assert.Equal([0, 1], inner.Pages);
        Assert.EndsWith("2,Name 2\r\n", await File.ReadAllTextAsync(path));
        await session.ExecuteQueryAsync("after export");
        Assert.True(inner.Queried);
        Assert.True(session.IsInTransaction);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("throw")]
    [InlineData("truncated")]
    [InlineData("cancel")]
    public async Task LaterPageFailureOrCancellationPreservesDestinationAndReleasesSession(string failure)
    {
        var inner = new PagedSession(failure);
        await using var session = new ExportDbSession(inner);
        var path = Path.Combine(_directory, "rows.csv");
        await File.WriteAllTextAsync(path, "old bytes");
        using var cancellation = new CancellationTokenSource();
        var source = new TableExportSource(_ => Task.FromResult<IDbSession>(session), "Customers", new([new("Id")], 0, 1), [new("Id", 0)]);
        var task = new ResultExporter(new Picker(path)).ExportAsync(source, new(), ExportScope.AllMatchingTableRows, cancellationToken: cancellation.Token);
        await inner.Fetching.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var query = session.ExecuteQueryAsync("unrelated");
        Assert.False(query.IsCompleted);
        Assert.False(inner.Queried);
        if (failure == "cancel") cancellation.Cancel();
        else inner.Continue.SetResult();
        var outcome = await task;
        await query.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(inner.Queried);
        Assert.True(session.IsInTransaction);
        Assert.Equal(failure == "cancel" ? ExportStatus.Canceled : ExportStatus.Failed, outcome.Status);
        // The bounded producer can begin page two before the writer consumes page one's row.
        Assert.InRange(outcome.RowsWritten, failure == "cancel" ? 0 : 1, 1);
        Assert.Equal("old bytes", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
        Assert.Equal([0, 1], inner.Pages);
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }

    private sealed class WrittenProgress : IProgress<ExportProgress>
    {
        public TaskCompletionSource FirstRow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(ExportProgress value) => FirstRow.TrySetResult();
    }

    private sealed class PagedSession(string failure) : IDbSession
    {
        public IDatabaseProvider Provider { get; } = new SqliteProvider();
        public bool IsInTransaction => true;
        public bool Queried { get; private set; }
        public List<int> Pages { get; } = [];
        public TaskCompletionSource Fetching { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task CancelAsync() => Task.CompletedTask;
        public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null)
        {
            Queried = true;
            return Task.FromResult(new QueryResult());
        }
        public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) => throw new InvalidOperationException();
        public async Task<TablePageResult> LoadTablePageAsync(string tableName, TableQuerySpec spec, QueryExecutionOptions? options = null)
        {
            Pages.Add(spec.PageIndex);
            if (spec.PageIndex == 0)
            {
                var page = CreatePage([1], true);
                if (failure == "complete") page.Result.ObjectRows[0][1] = new string('x', 100_000);
                return page;
            }
            Fetching.SetResult();
            await Continue.Task.WaitAsync(options!.CancellationToken);
            if (failure == "throw") throw new IOException("later page");
            if (failure == "complete") return CreatePage([2]);
            return new TablePageResult { Result = new QueryResult { ErrorMessage = failure == "error" ? "failed" : null, IsTruncated = failure == "truncated" } };
        }
        public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => throw new InvalidOperationException();
        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query, QueryExecutionOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(query, options);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
