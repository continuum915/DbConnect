using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.ScratchComparisons;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// MySQL backup with DbConnect's dump (mysql-duckdb-backup/01): from the Backup / Restore operations and from an automated
/// Backup job. Skipped unless the container lane's DBCONNECT_CONFORMANCE_MYSQL_ADMIN_CONNECTION_STRING is set.
/// </summary>
public sealed class MySqlBackupTests : IDisposable
{
    // m_orders references z_customers (reverse alphabetical), the view calls the function, and the trigger writes audit.
    private const string Fixture = """
        CREATE TABLE z_customers (id int PRIMARY KEY, name varchar(50) NOT NULL, note text) ENGINE=InnoDB;
        CREATE TABLE m_orders (id int PRIMARY KEY, customer_id int NOT NULL, qty int NOT NULL,
            CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES z_customers(id)) ENGINE=InnoDB;
        CREATE TABLE audit (order_id int, what varchar(50)) ENGINE=InnoDB;
        CREATE FUNCTION doubled(n int) RETURNS int DETERMINISTIC RETURN n * 2;
        CREATE VIEW order_totals AS SELECT id, doubled(qty) AS d FROM m_orders;
        DELIMITER $$
        CREATE TRIGGER orders_audit AFTER INSERT ON m_orders FOR EACH ROW BEGIN INSERT INTO audit VALUES (NEW.id, 'inserted'); END$$
        DELIMITER ;
        INSERT INTO z_customers VALUES (1, 'O''Brien', 'line one\nline two'), (2, 'héllo 日本語', NULL);
        INSERT INTO m_orders VALUES (10, 1, 3), (11, 2, 5);
        """;

    private static readonly string[] Tables = ["z_customers", "m_orders", "audit"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-backup-{Guid.NewGuid():N}");

    public MySqlBackupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ABackupLoadsIntoAnEmptyDatabaseAndAJobWritesTheSameScript()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("bak", Fixture);
        var target = await server.CreateDatabaseAsync("bakdst", null);
        var path = Path.Combine(_directory, "backup.sql");
        var log = new List<string>();

        await new BackupService().BackupAsync(source, path, new SynchronousProgress(log.Add), CancellationToken.None);

        Assert.StartsWith("Backing up", log[0]);
        Assert.Contains(log, line => line.StartsWith("Table ", StringComparison.Ordinal) && line.EndsWith(": m_orders", StringComparison.Ordinal));
        Assert.Contains("  m_orders: 2 rows", log);
        Assert.StartsWith("Backup complete: 6 rows from 3 tables", log[^1]);
        var script = await File.ReadAllTextAsync(path);
        await server.RunScriptAsync(target, script);
        foreach (var table in Tables)
        {
            Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        }
        Assert.Equal(await RowsAsync(source, "order_totals"), await RowsAsync(target, "order_totals"));

        var jobPath = Path.Combine(_directory, "job.sql");
        var record = await new AutomationService(dataDirectory: _directory).ExecuteJobAsync(
            new AutomationJob { Type = JobType.Backup, SqlOrFilePath = jobPath }, source, CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, record.Status);
        Assert.Equal(WithoutCreatedLine(script), WithoutCreatedLine(await File.ReadAllTextAsync(jobPath)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ABackupRestoresIntoAFreshDatabase()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("rst", Fixture);
        var target = await server.CreateDatabaseAsync("rstdst", null);
        var path = Path.Combine(_directory, "restore.sql");
        await new BackupService().BackupAsync(source, path, new Progress<string>(), CancellationToken.None);
        var log = new List<string>();

        await new BackupService().RestoreAsync(target, path, new SynchronousProgress(log.Add), CancellationToken.None);

        Assert.StartsWith("Restoring", log[0]);
        Assert.Contains(log, line => line.Contains("% (", StringComparison.Ordinal));
        Assert.Matches(@"^Restore complete: \d+ statements in \d\d:\d\d\.$", log[^1]);
        foreach (var table in Tables)
        {
            Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        }
        Assert.Equal(await RowsAsync(source, "order_totals"), await RowsAsync(target, "order_totals"));
        // The trigger came back too, and was created after the data, so loading did not fire it.
        await server.RunScriptAsync(target, "INSERT INTO m_orders VALUES (12, 1, 1)");
        Assert.EndsWith("Int32:12|String:inserted", await RowsAsync(target, "audit"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARestoreStopsAtTheFirstFailingStatementAndNamesIt()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var target = await server.CreateDatabaseAsync("rstbad", null);
        var path = Path.Combine(_directory, "bad.sql");
        await File.WriteAllTextAsync(path, """
            CREATE TABLE kept (id int PRIMARY KEY);
            INSERT INTO kept VALUES (1);
            INSERT INTO missing_table VALUES (2),
                (3);
            CREATE TABLE never (id int);
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BackupService().RestoreAsync(target, path, new Progress<string>(), CancellationToken.None));

        Assert.StartsWith("Statement 3 failed: INSERT INTO missing_table VALUES (2),… — ", error.Message);
        Assert.Contains("missing_table", error.Message[error.Message.IndexOf('—')..]);
        Assert.Contains("Statements 1–2 were applied and stay applied.", error.Message);
        await using var db = await SessionSetup.OpenAsync(target);
        Assert.Equal([1], (await db.ExecuteQueryAsync("SELECT id FROM kept")).ObjectRows.Select(row => Convert.ToInt32(row[0])));
        Assert.Empty((await db.ExecuteQueryAsync("SHOW TABLES LIKE 'never'")).ObjectRows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancelStopsARestoreBetweenStatementsAndSaysWhatStaysApplied()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var target = await server.CreateDatabaseAsync("rstcancel", null);
        var path = Path.Combine(_directory, "many.sql");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 2000).Select(i => i == 1
            ? "CREATE TABLE many (id int PRIMARY KEY);"
            : $"INSERT INTO many VALUES ({i});"));
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupService().RestoreAsync(target, path,
            new SynchronousProgress(line =>
            {
                // "37% (512 statements)": cancel well into the file.
                var count = Regex.Match(line, @"\((\d+) statements\)$");
                if (count.Success && int.Parse(count.Groups[1].Value) >= 500) cancellation.Cancel();
            }),
            cancellation.Token));

        var stoppedAt = int.Parse(Regex.Match(error.Message, @"^Restore cancelled before statement (\d+)\.").Groups[1].Value);
        Assert.InRange(stoppedAt, 501, 2000);
        Assert.EndsWith($"Statements 1–{stoppedAt - 1} were applied and stay applied.", error.Message);
        await using var db = await SessionSetup.OpenAsync(target);
        Assert.Equal(stoppedAt - 2L, Convert.ToInt64((await db.ExecuteQueryAsync("SELECT count(*) FROM many")).ObjectRows[0][0]));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancelStopsTheBackupAndPublishesNothing()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("bakcancel", Fixture);
        var path = Path.Combine(_directory, "out", "backup.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupService().BackupAsync(source, path,
            new SynchronousProgress(_ => cancellation.Cancel()), cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    // On the dispatcher, as in the app, so log lines arrive in order; the table is large enough that the dump is still
    // writing it when Cancel arrives.
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task TheDialogsCancelStopsABackupAndSaysNothingWasPublished()
    {
        await using var server = ScratchServer.For("MySQL");
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("bakdialog", """
            CREATE TABLE big (id int PRIMARY KEY, label varchar(20)) ENGINE=InnoDB;
            SET SESSION cte_max_recursion_depth = 300000;
            INSERT INTO big WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 200000) SELECT i, CONCAT('row ', i) FROM s;
            """);
        var path = Path.Combine(_directory, "dialog", "backup.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dialog = new ViewModels.BackupRestoreViewModel([source]) { TargetSession = source, OutputPath = path };
        dialog.LogLines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems?.OfType<string>().Any(line => line.StartsWith("Table ", StringComparison.Ordinal)) == true)
                dialog.CancelCommand.Execute(null);
        };

        Assert.True(dialog.CanCancel);
        await dialog.StartBackupCommand.ExecuteAsync(null);

        Assert.Equal("⏹ Backup cancelled; nothing was published.", dialog.LogLines[^1]);
        Assert.DoesNotContain(dialog.LogLines, line => line.StartsWith("Backup complete", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    private static string WithoutCreatedLine(string script) =>
        string.Join("\n", script.Split('\n').Where(line => !line.StartsWith("-- Created: ", StringComparison.Ordinal)));

    /// <summary>Reports on the calling thread, so the log is complete when the operation returns.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
