using Microsoft.Data.SqlClient;
using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.SqlServerExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class SqlServerExportInterruptionTests
{
    [SqlServerExportTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedRerunHonorsTimeoutOrSessionCancelAndLeavesUserTransactionOpen(bool cancel)
    {
        await using var session = await OpenAsync();
        await using var blocker = await OpenAsync();
        var table = $"rain_export_blocked_{Guid.NewGuid():N}";
        Task? reading = null;
        try
        {
            Assert.False((await session.ExecuteQueryAsync($"CREATE TABLE {table}(value int); INSERT INTO {table} VALUES (1)")).HasError);
            await session.BeginTransactionAsync();
            var selected = await session.ExecuteQueryAsync($"SELECT value FROM {table}",
                new QueryExecutionOptions { TimeoutSeconds = cancel ? 20 : 1 });
            Assert.False(selected.HasError, selected.Error?.Summary);
            var capture = Capture(session, selected);
            await blocker.BeginTransactionAsync();
            Assert.False((await blocker.ExecuteQueryAsync($"UPDATE {table} WITH (TABLOCKX) SET value=2")).HasError);
            reading = ReadAsync(capture);
            // The blocker owns an exclusive table lock; the SELECT cannot return a row.
            await Task.Delay(250);
            Assert.False(reading.IsCompleted);
            if (cancel)
            {
                await session.CancelAsync();
                var error = await Record.ExceptionAsync(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
                // SqlClient can surface command.Cancel as SqlException rather than task cancellation.
                Assert.True(error is OperationCanceledException or SqlException, error?.ToString());
            }
            else
            {
                var error = await Assert.ThrowsAsync<SqlException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(-2, error.Number);
            }
            await blocker.RollbackAsync();
            Assert.True(session.IsInTransaction);
            var state = await session.ExecuteQueryAsync("SELECT @@TRANCOUNT AS depth, XACT_STATE() AS state");
            Assert.False(state.HasError, state.Error?.Summary);
            Assert.Equal(new object?[] { 1, (short)1 }, Assert.Single(state.ObjectRows));
            await session.RollbackAsync();
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally
        {
            await session.CancelAsync();
            if (blocker.IsInTransaction) await blocker.RollbackAsync();
            if (reading != null) { try { await reading.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
            if (session.IsInTransaction) await session.RollbackAsync();
            await blocker.ExecuteQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task ReadAsync(QueryExportCapture capture)
    {
        await foreach (var row in capture.Source.ReadRowsAsync(default)) { }
    }
}
