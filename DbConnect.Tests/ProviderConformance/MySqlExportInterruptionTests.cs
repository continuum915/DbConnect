using MySqlConnector;
using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.MySqlExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class MySqlExportInterruptionTests
{
    [MySqlExportFact]
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

            Assert.False((await blocker.ExecuteQueryAsync($"LOCK TABLES {table} WRITE")).HasError);
            var error = await Record.ExceptionAsync(async () =>
            {
                await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
            }).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsAssignableFrom<MySqlException>(error);
            await blocker.ExecuteQueryAsync("UNLOCK TABLES");
            Assert.False(session.IsInTransaction);
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally
        {
            await blocker.ExecuteQueryAsync("UNLOCK TABLES");
            await session.ExecuteQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    [MySqlExportFact]
    public async Task CancelInterruptsServerWorkAndLeavesSessionUsable()
    {
        await using var session = await OpenAsync();
        await using var observer = await OpenAsync();
        var table = $"rain_export_delay_{Guid.NewGuid():N}";
        var view = $"rain_export_wait_{Guid.NewGuid():N}";
        using var stop = new CancellationTokenSource();
        Task? reading = null;
        try
        {
            var pid = Convert.ToUInt64(Assert.Single((await session.ExecuteQueryAsync("SELECT CONNECTION_ID()")).ObjectRows)[0]);
            Assert.False((await session.ExecuteQueryAsync($"CREATE TABLE {table}(delay double); INSERT INTO {table} VALUES (0); CREATE VIEW {view} AS SELECT SLEEP(delay) AS value FROM {table}")).HasError);


            var result = await session.ExecuteQueryAsync($"SELECT value FROM {view}", new QueryExecutionOptions { TimeoutSeconds = 20 });
            Assert.False(result.HasError, result.Error?.Summary);
            var capture = Capture(session, result);
            Assert.False((await observer.ExecuteQueryAsync($"UPDATE {table} SET delay=10")).HasError);
            reading = Task.Run(async () => { await foreach (var row in capture.Source.ReadRowsAsync(stop.Token)) { } });
            await WaitForSleepAsync(observer, pid);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(session.IsInTransaction);
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

    private static async Task WaitForSleepAsync(IDbSession observer, ulong pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await observer.ExecuteQueryAsync("SELECT STATE FROM information_schema.PROCESSLIST WHERE ID=@p0", new QueryExecutionOptions { Parameters = [pid] });
            Assert.False(status.HasError, status.Error?.Summary);
            if (status.ObjectRows.SingleOrDefault()?[0] is "User sleep") return;
            await Task.Delay(20);
        }
        Assert.Fail("Export backend never entered its controlled MySQL sleep gate.");
    }
}
