using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class SqliteQueryExportTests
{
    private static Task<IDbSession> OpenAsync() => new DatabaseService().CreateSession(new ConnectionInfo
    {
        Provider = "SQLite",
        ConnectionString = "Data Source=:memory:"
    }).Provider.OpenSessionAsync(new ConnectionInfo { ConnectionString = "Data Source=:memory:" });

    private static QueryExportCapture Capture(IDbSession session, QueryResult result) =>
        Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result)!;

    [Fact]
    public async Task StreamsRawDriverValuesAndClonedParametersInOriginalTransactionAndTempContext()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        await session.ExecuteQueryAsync("CREATE TEMP TABLE items(id INTEGER, label TEXT, payload BLOB, optional TEXT); INSERT INTO items VALUES(1,'first',x'0102',NULL),(2,'second',x'FF00','');");
        var parameters = new object?[] { 0L, new byte[] { 1, 2 } };
        var result = await session.ExecuteQueryAsync("SELECT id, label, payload, optional, @p1 AS bound FROM items WHERE id > @p0",
            new QueryExecutionOptions { MaxResultRows = 1, Parameters = parameters, TimeoutSeconds = 7 });
        Assert.True(result.IsTruncated);
        parameters[0] = 100L;
        ((byte[])parameters[1]!)[0] = 99;
        var source = Capture(session, result).Source;
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in source.ReadRowsAsync(default)) rows.Add(row);

        Assert.Equal(["id", "label", "payload", "optional", "bound"], source.Columns.Select(c => c.Name));
        Assert.Equal(2, rows.Count);
        Assert.Equal(1L, Assert.IsType<long>(rows[0][0]));
        Assert.Equal("first", Assert.IsType<string>(rows[0][1]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][2]));
        Assert.Null(rows[0][3]);
        Assert.Equal("", rows[1][3]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[1][4]));
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT id FROM items")).HasError);
        await session.RollbackAsync();
        Assert.False(session.IsInTransaction);
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("PRAGMA user_version")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("CREATE TABLE test(id INTEGER)")]
    [InlineData("BEGIN")]
    [InlineData("SELECT random()")]
    [InlineData("SELECT * FROM json_each('[1,2]')")]
    public async Task AmbiguousAndUnsupportedExecutionsHaveNoExportCapture(string sql)
    {
        await using var session = await OpenAsync();
        var result = await session.ExecuteQueryAsync(sql);
        Assert.Null(Capture(session, result));
    }

    [Fact]
    public async Task CannotCaptureForeignOrFabricatedResultsOrEndedTransactionContext()
    {
        await using var session = await OpenAsync();
        await using var other = await OpenAsync();
        await session.BeginTransactionAsync();
        var result = await session.ExecuteQueryAsync("SELECT 1 AS value");
        var capture = Capture(session, result);
        Assert.NotNull(capture);
        Assert.Null(Capture(other, result));
        Assert.Null(Capture(session, new QueryResult { ExecutedSql = "SELECT 1" }));
        await session.CommitAsync();
        Assert.False(capture.IsCurrent());
        Assert.Null(Capture(session, result));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
        });
        Assert.False((await session.ExecuteQueryAsync("SELECT 2")).HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderOwnsSessionUntilCanceledOrDisposedAndOtherSessionsRemainUsable(bool cancel)
    {
        await using var session = await OpenAsync();
        await using var other = await OpenAsync();
        await session.BeginTransactionAsync();
        var result = await session.ExecuteQueryAsync("SELECT 1 AS value UNION ALL SELECT 2 UNION ALL SELECT 3",
            new QueryExecutionOptions { MaxResultRows = 1 });
        using var stop = new CancellationTokenSource();
        var reader = Capture(session, result).Source.ReadRowsAsync(stop.Token).GetAsyncEnumerator();
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
        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).HasError);
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT 6")).HasError);
    }

    [Fact]
    public async Task CancelInterruptsNativeAggregateAndPreservesOpenTransaction()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        await session.ExecuteQueryAsync("CREATE TEMP TABLE items(value INTEGER); WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<10000) INSERT INTO items SELECT v FROM n;");
        var result = await session.ExecuteQueryAsync("SELECT sum(a.value * b.value) AS total FROM items a CROSS JOIN items b WHERE a.value > @p0",
            new QueryExecutionOptions { Parameters = [0L] });
        var capture = Capture(session, result);
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in capture.Source.ReadRowsAsync(stop.Token)) { }
        });
        Assert.Equal(9, Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(error.InnerException).SqliteErrorCode);
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT value FROM items LIMIT 1")).HasError);
    }
}
