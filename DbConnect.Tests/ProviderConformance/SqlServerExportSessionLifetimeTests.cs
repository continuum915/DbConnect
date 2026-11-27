using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.SqlServerExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class SqlServerExportSessionLifetimeTests
{
    [SqlServerExportTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderExcludesSameSessionWorkUntilCancellationOrDisposalAndOtherSessionsRemainUsable(bool cancel)
    {
        await using var session = await OpenAsync();
        await using var other = await OpenAsync();
        await session.BeginTransactionAsync();
        var result = await session.ExecuteQueryAsync("SELECT 1 AS value UNION ALL SELECT 2 UNION ALL SELECT 3", new QueryExecutionOptions { MaxResultRows = 1 });
        using var stop = new CancellationTokenSource();
        var reader = Capture(session, result).Source.ReadRowsAsync(stop.Token).GetAsyncEnumerator();
        try
        {
            Assert.True(await reader.MoveNextAsync());
            var waiting = session.ExecuteQueryAsync("SELECT 4");
            Assert.False(waiting.IsCompleted);
            Assert.False((await other.ExecuteQueryAsync("SELECT 5")).HasError);
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
            }
            await reader.DisposeAsync();
            var afterReader = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.IsInTransaction);
            Assert.False(afterReader.HasError, afterReader.Error?.Summary);
            Assert.False((await session.ExecuteQueryAsync("SELECT 6")).HasError);
        }
        finally { await reader.DisposeAsync(); }
    }

    [SqlServerExportFact]
    public async Task ForeignFabricatedDisposedOrEndedTransactionContextsCannotReplay()
    {
        var session = await OpenAsync();
        await using var other = await OpenAsync();
        try
        {
            await session.BeginTransactionAsync();
            var result = await session.ExecuteQueryAsync("SELECT 1 AS value");
            var capture = Capture(session, result);
            Assert.Null(((IQueryExportSession)other).CaptureQueryExport(result));
            Assert.Null(((IQueryExportSession)session).CaptureQueryExport(new QueryResult { ExecutedSql = "SELECT 1" }));
            await session.RollbackAsync();
            Assert.False(capture.IsCurrent());
            Assert.Null(((IQueryExportSession)session).CaptureQueryExport(result));
            var fresh = Capture(session, await session.ExecuteQueryAsync("SELECT 2 AS value"));
            await session.DisposeAsync();
            Assert.False(fresh.IsCurrent());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var row in fresh.Source.ReadRowsAsync(default)) { }
            });
        }
        finally { await session.DisposeAsync(); }
    }
}
