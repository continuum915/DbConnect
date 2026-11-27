using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class DuckDbQueryExportTests
{
    private static Task<IDbSession> OpenAsync() => new DuckDbProvider().OpenSessionAsync(
        new ConnectionInfo { ConnectionString = "Data Source=:memory:" });

    private static QueryExportCapture Capture(IDbSession session, QueryResult result) =>
        Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result)!;

    [Fact]
    public async Task StreamsRawValuesBeyondCapWithCapturedParametersAndOriginalTransaction()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        Assert.False((await session.ExecuteQueryAsync("CREATE TEMP TABLE items AS SELECT i::BIGINT AS id, 123456789012345678.123456789::DECIMAL(27,9) AS amount, true AS flag, TIMESTAMP '2026-10-19 12:34:56.123456' AS stamp, '\\x01\\x02'::BLOB AS payload, NULL::VARCHAR AS optional FROM range(1200) t(i)")).HasError);
        var parameters = new object?[] { 0L };
        var result = await session.ExecuteQueryAsync("SELECT id, id, amount, flag, stamp, payload, optional FROM items WHERE id >= ? ORDER BY id",
            new QueryExecutionOptions { MaxResultRows = 1, Parameters = parameters, TimeoutSeconds = 7 });
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.True(result.IsTruncated);
        parameters[0] = 2000L;
        var capture = Capture(session, result);
        Assert.NotNull(capture);
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in capture.Source.ReadRowsAsync(default)) rows.Add(row);
        Assert.Equal(1200, rows.Count);
        Assert.Equal(["id", "id", "amount", "flag", "stamp", "payload", "optional"], capture.Source.Columns.Select(c => c.Name));
        Assert.Equal(1199L, Assert.IsType<long>(rows[^1][0]));
        Assert.Equal(0L, Assert.IsType<long>(rows[0][1]));
        Assert.Equal(123456789012345678.123456789m, Assert.IsType<decimal>(rows[0][2]));
        Assert.True(Assert.IsType<bool>(rows[0][3]));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56).AddTicks(1234560), Assert.IsType<DateTime>(rows[0][4]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][5]));
        Assert.Null(rows[0][6]);
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        await session.RollbackAsync();
        Assert.False(capture.IsCurrent());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderExcludesOtherOperationsUntilCancellationOrDisposal(bool cancel)
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        var result = await session.ExecuteQueryAsync("SELECT 1 AS value UNION ALL SELECT 2 UNION ALL SELECT 3", new QueryExecutionOptions { MaxResultRows = 1 });
        using var stop = new CancellationTokenSource();
        var reader = Capture(session, result).Source.ReadRowsAsync(stop.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var waiting = session.ExecuteQueryAsync("SELECT 4");
        Assert.False(waiting.IsCompleted);
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        }
        await reader.DisposeAsync();
        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).HasError);
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT 5")).HasError);
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT random()")]
    [InlineData("SELECT * FROM range(3)")]
    [InlineData("PRAGMA version")]
    public async Task UnsupportedContextsHaveNoCapture(string sql)
    {
        await using var session = await OpenAsync();
        Assert.Null(Capture(session, await session.ExecuteQueryAsync(sql)));
    }

    [Fact]
    public async Task DisposedOrForeignSessionsCannotReplayResult()
    {
        var session = await OpenAsync();
        await using var other = await OpenAsync();
        var result = await session.ExecuteQueryAsync("SELECT 1 AS value");
        var capture = Capture(session, result);
        Assert.Null(Capture(other, result));
        Assert.Null(Capture(session, new QueryResult { ExecutedSql = "SELECT 1" }));
        await session.DisposeAsync();
        Assert.False(capture.IsCurrent());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
        });
    }

    [Fact]
    public async Task NativeCancellationLeavesTransactionOpenForUserRollbackAndSessionRemainsUsable()
    {
        await using var session = await OpenAsync();
        // The join takes about half a second, well past the 20 ms cancel.
        Assert.False((await session.ExecuteQueryAsync("CREATE TEMP TABLE items AS SELECT i AS value FROM range(30000) t(i)")).HasError);
        await session.BeginTransactionAsync();
        var result = await session.ExecuteQueryAsync("SELECT sum(a.value * b.value) AS total FROM items a CROSS JOIN items b");
        Assert.False(result.HasError);
        var capture = Capture(session, result);
        using var stop = new CancellationTokenSource();
        // Cancel from a dedicated thread: a CancellationTokenSource timer runs on the thread
        // pool, which a parallel test run can starve until the query has already finished.
        var canceller = new Thread(() => { Thread.Sleep(20); stop.Cancel(); }) { IsBackground = true };
        canceller.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in capture.Source.ReadRowsAsync(stop.Token)) { }
        });
        canceller.Join();
        Assert.True(session.IsInTransaction);
        // DuckDB marks a transaction aborted on a native interrupt. Export must not
        // silently roll it back: the user retains control of that boundary.
        await session.RollbackAsync();
        Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
    }

    [Fact]
    public async Task CapturedTimeoutInterruptsUncappedRerun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-duck-timeout-{Guid.NewGuid():N}.duckdb");
        var info = new ConnectionInfo { ConnectionString = $"Data Source={path}" };
        try
        {
            await using var session = await new DuckDbProvider().OpenSessionAsync(info);
            await session.ExecuteQueryAsync("CREATE TABLE items AS SELECT i AS value FROM range(10) t(i)");
            var result = await session.ExecuteQueryAsync("SELECT sum(a.value * b.value) AS total FROM items a CROSS JOIN items b",
                new QueryExecutionOptions { TimeoutSeconds = 1 });
            Assert.False(result.HasError);
            var capture = Capture(session, result);
            await using var other = await new DuckDbProvider().OpenSessionAsync(info);
            await other.ExecuteQueryAsync("INSERT INTO items SELECT i FROM range(10,100000) t(i)");
            var error = await Record.ExceptionAsync(async () =>
            {
                await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
            });
            Assert.NotNull(error);
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally { File.Delete(path); }
    }
}
