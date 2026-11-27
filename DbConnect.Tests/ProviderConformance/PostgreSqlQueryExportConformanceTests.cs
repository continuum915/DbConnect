using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.PostgreSqlExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class PostgreSqlQueryExportConformanceTests
{
    [PostgreSqlExportTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutableArrayAndListParametersAreCapturedBeforeCallerChanges(bool list)
    {
        await using var session = await OpenAsync();
        System.Collections.Generic.IList<int> values = list ? new List<int> { 1, 2 } : new[] { 1, 2 };
        var result = await session.ExecuteQueryAsync("SELECT @p0::integer[] AS values", new QueryExecutionOptions { Parameters = [values] });
        Assert.False(result.HasError, result.Error?.Summary);
        var capture = Capture(session, result);
        values[0] = 99;
        await foreach (var row in capture.Source.ReadRowsAsync(default))
            Assert.Equal(new[] { 1, 2 }, Assert.IsType<int[]>(row[0]));
    }

    [PostgreSqlExportFact]
    public async Task StreamsRawValuesBeyondCapWithCapturedParametersAndOriginalTransaction()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        var setup = await session.ExecuteQueryAsync("""
            CREATE TEMP TABLE items AS SELECT i::bigint AS id,
                123456789012345678.123456789::numeric(27,9) AS amount, true AS flag,
                timestamp '2026-10-19 12:34:56.123456' AS stamp,
                timestamptz '2026-10-19 12:34:56.123456+00' AS utc,
                decode('0102','hex') AS payload, NULL::text AS optional, ''::text AS empty, 'NULL'::text AS literal
            FROM generate_series(0,1199) AS i
            """);
        Assert.False(setup.HasError, setup.Error?.Summary);
        var parameters = new object?[] { "0", new byte[] { 1, 2 } };
        var result = await session.ExecuteQueryAsync("SELECT id, id, amount, flag, stamp, utc, payload, optional, empty, literal, @p1::bytea AS bound FROM items WHERE id >= @p0 ORDER BY id",
            new QueryExecutionOptions { Parameters = parameters, MaxResultRows = 1, TimeoutSeconds = 7 });
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.True(result.IsTruncated);
        parameters[0] = "2000";
        ((byte[])parameters[1]!)[0] = 99;
        var capture = Capture(session, result);
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in capture.Source.ReadRowsAsync(default)) rows.Add(row);
        Assert.Equal(1200, rows.Count);
        Assert.Equal(["id", "id", "amount", "flag", "stamp", "utc", "payload", "optional", "empty", "literal", "bound"], capture.Source.Columns.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(0, 11), capture.Source.Columns.Select(c => c.Ordinal));
        Assert.Equal(typeof(decimal), capture.Source.Columns[2].ClrType);
        Assert.Equal("numeric", capture.Source.Columns[2].ProviderTypeName);
        Assert.Equal(1199L, Assert.IsType<long>(rows[^1][0]));
        Assert.Equal(0L, Assert.IsType<long>(rows[0][1]));
        Assert.Equal(123456789012345678.123456789m, Assert.IsType<decimal>(rows[0][2]));
        Assert.True(Assert.IsType<bool>(rows[0][3]));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56).AddTicks(1234560), Assert.IsType<DateTime>(rows[0][4]));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(rows[0][5]).Kind);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][6]));
        Assert.Null(rows[0][7]);
        Assert.Equal("", rows[0][8]);
        Assert.Equal("NULL", rows[0][9]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][10]));
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT id FROM items LIMIT 1")).HasError);
        await session.RollbackAsync();
        Assert.False(capture.IsCurrent());
    }

    [PostgreSqlExportTheory]
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
            // Server-side cancellation can abort the transaction; export must leave rollback to the user.
            if (cancel) await session.RollbackAsync();
            if (!cancel) Assert.False(afterReader.HasError, afterReader.Error?.Summary);
            Assert.False((await session.ExecuteQueryAsync("SELECT 6")).HasError);
        }
        finally { await reader.DisposeAsync(); }
    }

    [PostgreSqlExportTheory]
    [InlineData("SELECT $$semi;colon$$ AS value", true)]
    [InlineData("SELECT 1 /* outer /* nested */ comment */ AS value", true)]
    [InlineData("SELECT 1 INTO TEMP exported_table", false)]
    [InlineData("SELECT value FROM (SELECT 1 AS value) t FOR UPDATE", false)]
    [InlineData("SELECT random()", false)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", false)]
    [InlineData("SELECT 1; SELECT 2", false)]
    public async Task OnlyEligibleRecordedResultsCanBeCaptured(string sql, bool eligible)
    {
        await using var session = await OpenAsync();
        var result = await session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.Equal(eligible, ((IQueryExportSession)session).CaptureQueryExport(result) != null);
    }

    [PostgreSqlExportFact]
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
