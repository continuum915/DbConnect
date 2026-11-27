using System.Text.RegularExpressions;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Dump SQL File on large tables: paging, bounded memory and cancel (sql-dump-and-insert-export/09).</summary>
[Collection(MemoryMeasurement.Name)]
public sealed class DatabaseDumpStreamingTests : DumpTestDatabases
{
    private const int Rows = 12_000;
    private const int Million = 1_000_000;

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task AMillionRowTableDumpsInBoundedMemory(string provider)
    {
        var source = await SessionAsync(provider, "million", Numbers(provider, "big", Million));
        var path = Path.Combine(DumpDirectory, "million.sql");
        var memory = new LiveHeapEvery(200_000);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path, progress: memory);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.Equal(Million, outcome.RowsWritten);
        Assert.True(new FileInfo(path).Length > 60_000_000, "the script is far larger than the bound below");
        // Live heap after a full collection at 200k, 400k … 1M rows: holding the rows would add ~100 MB by the end.
        Assert.Equal(5, memory.Samples.Count);
        var growth = memory.Samples.Max() - memory.Samples[0];
        Assert.True(growth < 16_000_000, $"live heap grew {growth / 1_000_000} MB: {string.Join(", ", memory.Samples.Select(b => b / 1_000_000))} MB");
        var tail = File.ReadLines(path).Where(l => l.StartsWith("INSERT", StringComparison.Ordinal)).TakeLast(1).Single();
        Assert.Contains($"VALUES ({Million}, 'row {Million}');", tail);
    }

    [Fact]
    public async Task CancelHalfwayThroughAMillionRowTablePublishesNothing()
    {
        var source = await SessionAsync("SQLite", "million-cancel", Numbers("SQLite", "big", Million));
        var path = Path.Combine(DumpDirectory, "out", "million.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterRows(Million / 2, cancellation);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path,
            progress: progress, cancellationToken: cancellation.Token);

        Assert.Equal(ExportStatus.Canceled, outcome.Status);
        Assert.InRange(progress.LastRows, Million / 2, Million - 1);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    private static string Numbers(string provider, string table, int count) => provider == "SQLite"
        ? $"""
          CREATE TABLE {table} (id INTEGER PRIMARY KEY, label TEXT);
          WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < {count}) INSERT INTO {table} SELECT i, 'row ' || i FROM s;
          """
        : $"""
          CREATE TABLE {table} (id INTEGER PRIMARY KEY, label VARCHAR);
          INSERT INTO {table} SELECT i, 'row ' || i FROM range(1, {count + 1}) t(i);
          """;

    // DuckDB sorts an ENUM by its declaration order; 'zeta' is declared first, so text order would be the reverse.
    [Fact]
    public async Task AnEnumKeyLargerThanAPageIsReadInFull()
    {
        var source = await SessionAsync("DuckDB", "enum", $"""
            CREATE TYPE mood AS ENUM ('zeta', 'mid', 'alpha');
            CREATE TABLE moods (m mood, id INTEGER, PRIMARY KEY (m, id));
            INSERT INTO moods SELECT (['zeta', 'mid', 'alpha'])[i % 3 + 1]::mood, i FROM range(1, {Rows + 1}) t(i);
            """);
        var path = Path.Combine(DumpDirectory, "enum.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.ForTables(Table("moods")), DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var ids = Regex.Matches(await File.ReadAllTextAsync(path), @"', (\d+)\);").Select(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(Enumerable.Range(1, Rows), ids.Order());
    }

    private const string SingleKey = "id INTEGER PRIMARY KEY, label TEXT";
    private const string CompositeKey = "grp TEXT NOT NULL, id INTEGER NOT NULL, label TEXT, PRIMARY KEY (grp, id)";

    // SQLite reads each table as it stands, so by OFFSET a write between pages moves later rows across the page
    // boundary: deleting rows already read skips as many, and adding rows before the boundary repeats as many.
    [Theory]
    [InlineData(SingleKey, "i, 'row ' || i", "DELETE FROM big WHERE id <= 100;")]
    [InlineData(SingleKey, "i, 'row ' || i", "INSERT INTO big SELECT -id, 'early' FROM big WHERE id <= 100;")]
    [InlineData(CompositeKey, "'g' || (i % 7), i, 'row ' || i", "DELETE FROM big WHERE grp = 'g0' AND id <= 300;")]
    [InlineData(CompositeKey, "'g' || (i % 7), i, 'row ' || i", "INSERT INTO big SELECT grp, -id, 'early' FROM big WHERE id <= 300;")]
    public async Task RowsWrittenBetweenPagesNeitherSkipNorRepeatTheRowsThatWereThere(string columns, string values, string write)
    {
        var source = await SessionAsync("SQLite", "paging", $"""
            CREATE TABLE big ({columns});
            WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < {Rows}) INSERT INTO big SELECT {values} FROM s;
            """);
        var path = Path.Combine(DumpDirectory, "paging.sql");
        var writer = new WriteAfterRows(1000, () => ExecuteScriptAsync(source, write));

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path, progress: writer);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.True(writer.Wrote);
        var ids = Regex.Matches(await File.ReadAllTextAsync(path), @"(-?\d+), 'row \d+'\);").Select(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(Enumerable.Range(1, Rows), ids.Order());
    }

    // SQLite allows NULL in a key that is not INTEGER PRIMARY KEY and sorts NULLs first; a page ending on one goes on by OFFSET.
    [Fact]
    public async Task AKeyHoldingMoreNullsThanAPageIsReadInFull()
    {
        var source = await SessionAsync("SQLite", "nulls", $"""
            CREATE TABLE coded (code TEXT PRIMARY KEY, n INTEGER NOT NULL);
            WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < {Rows}) INSERT INTO coded SELECT CASE WHEN i <= 6000 THEN NULL ELSE 'c' || i END, i FROM s;
            """);
        var path = Path.Combine(DumpDirectory, "nulls.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var target = await SessionAsync("SQLite", "nulls-target", null);
        // One transaction, so the 12,000 INSERTs are not 12,000 commits.
        await ExecuteScriptAsync(target, $"BEGIN;\n{await File.ReadAllTextAsync(path)}\nCOMMIT;");
        Assert.Equal(await QueryAsync(source, "SELECT count(*), sum(n), count(code) FROM coded"), await QueryAsync(target, "SELECT count(*), sum(n), count(code) FROM coded"));
    }

    private sealed class LiveHeapEvery(long rows) : IProgress<DumpProgress>
    {
        public List<long> Samples { get; } = [];
        public void Report(DumpProgress value)
        {
            if (value.Rows is { } read && read > 0 && read % rows == 0) Samples.Add(GC.GetTotalMemory(forceFullCollection: true));
        }
    }

    private sealed class CancelAfterRows(long rows, CancellationTokenSource cancellation) : IProgress<DumpProgress>
    {
        public long LastRows { get; private set; }
        public void Report(DumpProgress value)
        {
            if (value.Rows is not { } read) return;
            LastRows = read;
            if (read >= rows) cancellation.Cancel();
        }
    }

    private sealed class WriteAfterRows(long rows, Func<Task> write) : IProgress<DumpProgress>
    {
        public bool Wrote { get; private set; }
        public void Report(DumpProgress value)
        {
            if (Wrote || value.Rows is not { } read || read < rows) return;
            Wrote = true;
            Task.Run(write).GetAwaiter().GetResult();
        }
    }
}

/// <summary>Tests that read the process's live heap run alone, so other tests' allocations do not count against them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurement
{
    public const string Name = "Memory measurement";
}
