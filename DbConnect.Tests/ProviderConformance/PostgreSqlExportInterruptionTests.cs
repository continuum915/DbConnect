using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.PostgreSqlExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class PostgreSqlExportInterruptionTests
{
    [PostgreSqlExportFact]
    public async Task CapturedCommandTimeoutInterruptsBlockedRerunAndSessionRemainsUsable()
    {
        await using var session = await OpenAsync();
        await using var blocker = await OpenAsync();
        var table = $"rain_export_timeout_{Guid.NewGuid():N}";
        try
        {
            Assert.False((await session.ExecuteQueryAsync($"CREATE TABLE {table}(value integer); INSERT INTO {table} VALUES (1)")).HasError);
            var result = await session.ExecuteQueryAsync($"SELECT value FROM {table}", new QueryExecutionOptions { TimeoutSeconds = 1 });
            Assert.False(result.HasError, result.Error?.Summary);
            var capture = Capture(session, result);
            await blocker.BeginTransactionAsync();
            Assert.False((await blocker.ExecuteQueryAsync($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE")).HasError);
            var error = await Record.ExceptionAsync(async () =>
            {
                await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
            }).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsAssignableFrom<NpgsqlException>(error);
            await blocker.RollbackAsync();
            Assert.False(session.IsInTransaction);
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally
        {
            if (blocker.IsInTransaction) await blocker.RollbackAsync();
            await session.ExecuteQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    [PostgreSqlExportFact]
    public async Task CancelInterruptsServerWorkWithoutEndingOriginalUserTransaction()
    {
        await using var session = await OpenAsync();
        await using var observer = await OpenAsync();
        var table = $"rain_export_delay_{Guid.NewGuid():N}";
        var view = $"rain_export_wait_{Guid.NewGuid():N}";
        using var stop = new CancellationTokenSource();
        Task? reading = null;
        try
        {
            var pid = Assert.IsType<int>(Assert.Single((await session.ExecuteQueryAsync("SELECT pg_backend_pid()")).ObjectRows)[0]);
            Assert.False((await session.ExecuteQueryAsync($"CREATE TABLE {table}(delay double precision); INSERT INTO {table} VALUES (0); CREATE VIEW {view} AS SELECT 1 AS value FROM {table} CROSS JOIN LATERAL pg_sleep(delay)")).HasError);
            await session.BeginTransactionAsync();
            var result = await session.ExecuteQueryAsync($"SELECT value FROM {view}", new QueryExecutionOptions { TimeoutSeconds = 20 });
            Assert.False(result.HasError, result.Error?.Summary);
            var capture = Capture(session, result);
            Assert.False((await observer.ExecuteQueryAsync($"UPDATE {table} SET delay=10")).HasError);
            reading = Task.Run(async () => { await foreach (var row in capture.Source.ReadRowsAsync(stop.Token)) { } });
            await WaitForSleepAsync(observer, pid);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(session.IsInTransaction);
            // PostgreSQL aborts an interrupted transaction; only an explicit user rollback ends it.
            Assert.True((await session.ExecuteQueryAsync("SELECT 1")).HasError);
            await session.RollbackAsync();
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally
        {
            stop.Cancel();
            if (reading != null) { try { await reading; } catch { } }
            if (session.IsInTransaction) await session.RollbackAsync();
            await observer.ExecuteQueryAsync($"DROP VIEW IF EXISTS {view}; DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task WaitForSleepAsync(IDbSession observer, int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await observer.ExecuteQueryAsync("SELECT wait_event FROM pg_stat_activity WHERE pid=@p0", new QueryExecutionOptions { Parameters = [pid] });
            Assert.False(status.HasError, status.Error?.Summary);
            if (status.ObjectRows.SingleOrDefault()?[0] is "PgSleep") return;
            await Task.Delay(20);
        }
        Assert.Fail("Export backend never entered its controlled PostgreSQL sleep gate.");
    }
}
