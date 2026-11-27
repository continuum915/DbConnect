using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Dump SQL File ▸ Structure and Data (sql-dump-and-insert-export/05).</summary>
public sealed class DatabaseDumpDataTests : DumpTestDatabases
{
    // Alphabetical order is the reverse of foreign-key order, so data written unsorted cannot load with keys checked.
    // a_lines.doubled is generated, so the engine refuses a value for it.
    private const string Fixture = """
        CREATE TABLE z_customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL UNIQUE, note VARCHAR);
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES z_customers(id), placed DATE, total DECIMAL(10,2));
        CREATE TABLE a_lines (order_id INTEGER REFERENCES m_orders(id), line INTEGER, qty INTEGER CHECK (qty > 0), doubled INTEGER GENERATED ALWAYS AS (qty * 2) VIRTUAL, PRIMARY KEY (order_id, line));
        INSERT INTO z_customers VALUES (1, 'O''Brien', 'line one
        line two'), (2, 'héllo 日本語', NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31', 12.50), (11, 2, NULL, NULL), (12, 1, '2024-02-29', 0.01);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (12, 1, 7);
        """;

    private static readonly string[] Tables = ["z_customers", "m_orders", "a_lines"];


    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task StructureAndDataDumpOfAForeignKeyChainLoadsIntoAnEmptyDatabase(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture);
        var path = Path.Combine(DumpDirectory, "dump.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.Equal(8, outcome.RowsWritten);
        var script = await File.ReadAllTextAsync(path);
        var structures = Tables.Select(t => script.IndexOf($"\n-- Table main.{t}\n", StringComparison.Ordinal)).ToList();
        var data = Tables.Select(t => script.IndexOf($"\n-- Data main.{t}\n", StringComparison.Ordinal)).ToList();
        Assert.All(structures.Concat(data), p => Assert.True(p > 0, script));
        Assert.Equal(structures.Order(), structures);
        Assert.Equal(data.Order(), data);
        Assert.True(structures.Max() < data.Min(), "every table is created before any data is written");

        var target = await SessionAsync(provider, "target", null);
        await ExecuteScriptAsync(target, script);
        foreach (var table in Tables)
        {
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(target, table));
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        }
    }

    [Theory]
    [InlineData("DuckDB", "-- Data: read in one snapshot (one read transaction)")]
    [InlineData("SQLite", "-- Data: not a single snapshot; each table was read as it stood when the dump reached it")]
    public async Task HeaderStatesTheDataAndWhetherItIsOneSnapshot(string provider, string snapshotLine)
    {
        var source = await SessionAsync(provider, "header", Fixture);
        var path = Path.Combine(DumpDirectory, "header.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.ForTables(Table("z_customers")),
            DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        var header = script[..script.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');
        Assert.Equal("-- DbConnect SQL dump (structure and data)", header[0]);
        Assert.Equal("-- Includes: 1 table, table data", header[5]);
        Assert.Equal("-- Excludes: views, routines and other objects outside these tables; users, roles and grants", header[6]);
        Assert.Equal(snapshotLine, header[7]);
    }

    [Theory]
    [InlineData("DuckDB", false)]
    [InlineData("SQLite", true)]
    public async Task ARowWrittenDuringTheDumpIsLeftOutOnlyWhenTheDataIsOneSnapshot(string provider, bool included)
    {
        var source = await SessionAsync(provider, "concurrent", Fixture);
        var path = Path.Combine(DumpDirectory, "concurrent.sql");
        var writer = new WriteOnFirstDataProgress(() => ExecuteScriptAsync(source, "INSERT INTO a_lines (order_id, line, qty) VALUES (11, 1, 5);"));

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path,
            progress: writer);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.True(writer.Wrote);
        Assert.Equal(included, (await File.ReadAllTextAsync(path)).Contains("(11, 1, 5)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValueWithNoLiteralFailsTheDumpNamingTableAndColumnAndPublishesNothing()
    {
        var source = await SessionAsync("DuckDB", "literal", Fixture + "CREATE TABLE tagged (id INTEGER PRIMARY KEY, tags INTEGER[]); INSERT INTO tagged VALUES (1, [1, 2]);");
        var path = Path.Combine(DumpDirectory, "out", "literal.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.StartsWith("Table main.tagged could not be dumped: Column \"tags\"", outcome.Diagnostic);
        Assert.EndsWith("Destination was not published.", outcome.Diagnostic);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ProgressReportsTheTableAndRowsAndCancelDuringDataPublishesNothing()
    {
        var source = await SessionAsync("SQLite", "cancel", Fixture);
        var path = Path.Combine(DumpDirectory, "out", "cancel.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnFirstData(cancellation);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path,
            progress: progress, cancellationToken: cancellation.Token);

        Assert.Equal(ExportStatus.Canceled, outcome.Status);
        Assert.Equal(new DumpProgress(1, 3, "main.z_customers", 0), progress.First);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task TriggersAreCreatedAfterTheDataSoLoadingDoesNotFireThem()
    {
        var source = await SessionAsync("SQLite", "triggers", Fixture + """
            CREATE TABLE audit (what TEXT);
            CREATE TRIGGER orders_audit AFTER INSERT ON m_orders BEGIN INSERT INTO audit VALUES ('order ' || NEW.id); END;
            INSERT INTO m_orders VALUES (13, 2, NULL, 1);
            """);
        var path = Path.Combine(DumpDirectory, "triggers.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        Assert.True(script.IndexOf("\n-- Triggers main.m_orders\n", StringComparison.Ordinal) > script.IndexOf("\n-- Data main.a_lines\n", StringComparison.Ordinal), script);
        var target = await SessionAsync("SQLite", "triggers-target", null);
        await ExecuteScriptAsync(target, script);
        Assert.Equal(await RowsAsync(source, "audit", "1"), await RowsAsync(target, "audit", "1"));
        Assert.Equal(await SnapshotAsync(source, "m_orders"), await SnapshotAsync(target, "m_orders"));
    }

    [Fact]
    public async Task GeneratedKeysLoadAsTheyWereAndKeepGeneratingAfterTheHighestLoadedKey()
    {
        // Counters continue after the highest loaded key, not after deleted rows above it (as PostgreSQL's setval to max + 1).
        var source = await SessionAsync("SQLite", "keys", """
            CREATE TABLE k_events (id INTEGER PRIMARY KEY AUTOINCREMENT, what TEXT);
            INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
            DELETE FROM k_events WHERE what = 'two';
            """);
        var path = Path.Combine(DumpDirectory, "keys.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.ForTables(Table("k_events")), DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var target = await SessionAsync("SQLite", "keys-target", null);
        await ExecuteScriptAsync(target, await File.ReadAllTextAsync(path));
        foreach (var session in new[] { source, target })
            await ExecuteScriptAsync(session, "INSERT INTO k_events (what) VALUES ('next');");
        Assert.Equal(await RowsAsync(source, "k_events"), await RowsAsync(target, "k_events"));
    }

    [Fact]
    public async Task AStandAloneSequenceIsAStatedPrerequisiteNotPartOfTheDump()
    {
        var source = await SessionAsync("DuckDB", "sequence", """
            CREATE SEQUENCE ev_seq START 5;
            CREATE TABLE k_events (id INTEGER DEFAULT nextval('ev_seq') PRIMARY KEY, what VARCHAR);
            INSERT INTO k_events (what) VALUES ('one');
            """);
        var path = Path.Combine(DumpDirectory, "sequence.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        var header = script[..script.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("-- Prerequisites (must already exist; not scripted):\n--   ", header);
        Assert.Contains("ev_seq", header);
        Assert.DoesNotContain("CREATE SEQUENCE", script);
    }

    private sealed class WriteOnFirstDataProgress(Func<Task> write) : IProgress<DumpProgress>
    {
        public bool Wrote { get; private set; }
        public void Report(DumpProgress value)
        {
            // After a row is read, so the snapshot is certainly taken.
            if (Wrote || value.Rows is null or 0) return;
            Wrote = true;
            Task.Run(write).GetAwaiter().GetResult();
        }
    }

    private sealed class CancelOnFirstData(CancellationTokenSource cancellation) : IProgress<DumpProgress>
    {
        public DumpProgress? First { get; private set; }
        public void Report(DumpProgress value)
        {
            if (value.Rows == null) return;
            First ??= value;
            cancellation.Cancel();
        }
    }
}
