using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using SQLitePCL;

namespace DbConnect.Tests;

public sealed class SqliteTableExportCancellationTests
{
    [Fact]
    public async Task CancellationInterruptsNativeFetchAfterSqliteAcquiresItsReadLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-native-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "source.db")};Pooling=False";
        var path = Path.Combine(directory, "rows.csv");
        await using var session = new ExportDbSession(await new SqliteProvider().OpenSessionAsync(
            new ConnectionInfo { ConnectionString = connectionString }));
        try
        {
            Assert.False((await session.ExecuteQueryAsync("CREATE TABLE items(value); WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<10000) INSERT INTO items SELECT v FROM n; CREATE VIEW expensive AS SELECT sum(a.value*b.value*c.value) AS total FROM items a CROSS JOIN items b CROSS JOIN items c;")).HasError);
            await File.WriteAllTextAsync(path, "previous bytes");
            await using var probe = new SqliteConnection(connectionString);
            await probe.OpenAsync();
            using var stop = new CancellationTokenSource();
            var source = new TableExportSource(_ => Task.FromResult<IDbSession>(session), "expensive", new([], 0, 10), [new("total", 0)]);
            var operation = new ResultExporter(new Picker(path)).ExportAsync(source, new(),
                ExportScope.AllMatchingTableRows, cancellationToken: stop.Token);
            try
            {
                // No user transaction is open in this test. Only the native SELECT can hold
                // the file's read lock. Probe with no busy timeout, avoiding driver retry delays.
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    watchdog.Token.ThrowIfCancellationRequested();
                    var code = raw.sqlite3_exec(probe.Handle!, "BEGIN EXCLUSIVE", out _);
                    if (code == raw.SQLITE_BUSY) break;
                    Assert.Equal(raw.SQLITE_OK, code);
                    Assert.Equal(raw.SQLITE_OK, raw.sqlite3_exec(probe.Handle!, "ROLLBACK", out _));
                    await Task.Yield();
                }
                stop.Cancel();
                var outcome = await operation.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(ExportStatus.Canceled, outcome.Status);
                Assert.Equal(0, outcome.RowsWritten);
                Assert.Equal("previous bytes", await File.ReadAllTextAsync(path));
                Assert.DoesNotContain(Directory.GetFiles(directory), file => Path.GetFileName(file).StartsWith(".dbconnect-export-"));
                Assert.False((await session.ExecuteQueryAsync("SELECT sum(value) FROM items")).HasError);
            }
            finally { stop.Cancel(); await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally
        {
            await session.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CancellationInterruptsExpensivePagePreservesDestinationAndOriginalTransaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-native-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rows.csv");
        await File.WriteAllTextAsync(path, "previous bytes");
        await using var session = new ExportDbSession(await new SqliteProvider().OpenSessionAsync(
            new ConnectionInfo { ConnectionString = "Data Source=:memory:" }));
        try
        {
            await session.BeginTransactionAsync();
            Assert.False((await session.ExecuteQueryAsync("CREATE TEMP VIEW expensive AS WITH RECURSIVE n(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<1000000000) SELECT sum(value) AS total FROM n;")).HasError);
            var source = new TableExportSource(_ => Task.FromResult<IDbSession>(session), "expensive", new([], 0, 10), [new("total", 0)]);
            using var stop = new CancellationTokenSource();
            // SQLite evaluates the aggregate before returning any row. The timeout is a safety
            // bound; cancellation must finish without evaluating all one billion input rows.
            stop.CancelAfter(100);
            var outcome = await Task.Run(() => new ResultExporter(new Picker(path)).ExportAsync(source, new(),
                ExportScope.AllMatchingTableRows, cancellationToken: stop.Token)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ExportStatus.Canceled, outcome.Status);
            Assert.Equal(0, outcome.RowsWritten);
            Assert.Equal("previous bytes", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
            Assert.True(session.IsInTransaction);
            Assert.False((await session.ExecuteQueryAsync("CREATE TEMP TABLE retained(value); INSERT INTO retained VALUES(42); SELECT * FROM retained;")).HasError);
            await session.RollbackAsync();
            Assert.False(session.IsInTransaction);
            Assert.True((await session.ExecuteQueryAsync("SELECT * FROM retained")).HasError);
            // Exceeds the native progress interval: a leaked handler would still see the canceled token.
            var subsequent = await session.ExecuteQueryAsync("WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<10000) SELECT sum(v) FROM n;");
            Assert.False(subsequent.HasError);
            Assert.Equal(50005000L, subsequent.ObjectRows[0][0]);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
