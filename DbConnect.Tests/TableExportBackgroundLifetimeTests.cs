using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class TableExportBackgroundLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriterFailureOrEarlyDisposalCancelsInFlightFetchAndReleasesOriginalSession(bool writerFailure)
    {
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = 0;
        var inner = new Mock<IDbSession>();
        inner.SetupGet(s => s.IsInTransaction).Returns(true);
        inner.Setup(s => s.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<QueryExecutionOptions>())).ReturnsAsync(new QueryResult());
        inner.Setup(s => s.LoadTablePageAsync(It.IsAny<string>(), It.IsAny<TableQuerySpec>(), It.IsAny<QueryExecutionOptions>()))
            .Returns((string _, TableQuerySpec query, QueryExecutionOptions options) =>
            {
                Interlocked.Increment(ref pages);
                if (query.PageIndex == 0) return Task.FromResult(new TablePageResult
                {
                    HasNextPage = true,
                    Result = new QueryResult
                    {
                        ColumnInfos = [new ColumnInfo { Name = "value" }],
                        ObjectRows = [[new string('x', 100000)]]
                    }
                });
                fetching.TrySetResult();
                try
                {
                    if (!options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Background fetch was not stopped.");
                    options.CancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new TablePageResult());
                }
                finally { stopped.TrySetResult(); }
            });
        await using var session = new ExportDbSession(inner.Object);
        var source = new TableExportSource(_ => Task.FromResult<IDbSession>(session), "items", new([], 0, 1), [new("value", 0)]);
        if (writerFailure)
        {
            var files = new FailingFiles(fetching.Task);
            var outcome = await new ResultExporter(new Picker(), files).ExportAsync(source, new(), ExportScope.AllMatchingTableRows)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ExportStatus.Failed, outcome.Status);
            Assert.True(files.Deleted);
            Assert.False(files.Published);
        }
        else
        {
            await using var reader = source.ReadRowsAsync(default).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            await fetching.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = session.ExecuteQueryAsync("SELECT 1");
            Assert.False(queued.IsCompleted);
            await reader.DisposeAsync();
            Assert.False((await queued.WaitAsync(TimeSpan.FromSeconds(10))).HasError);
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, pages);
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT 2")).HasError);
        inner.Verify(s => s.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        inner.Verify(s => s.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Picker : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), "unused.csv"));
    }

    private sealed class FailingFiles(Task fetching) : IExportFileOperations
    {
        public bool Deleted { get; private set; }
        public bool Published { get; private set; }
        public Stream CreateTemporary(string path) => new FailingStream(fetching);
        public void DeleteTemporary(string path) => Deleted = true;
        public void Publish(string temporaryPath, string destinationPath) => Published = true;
    }

    private sealed class FailingStream(Task fetching) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await fetching.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            throw new IOException("writer failed");
        }
    }
}
