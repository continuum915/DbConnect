using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Dump SQL File ▸ Structure Only (sql-dump-and-insert-export/04).</summary>
public sealed class DatabaseDumpStructureTests : DumpTestDatabases
{
    // Alphabetical order is the reverse of foreign-key order, so a dump that is not sorted cannot load in DuckDB.
    private const string Fixture = """
        CREATE TABLE z_customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL UNIQUE);
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES z_customers(id), placed DATE);
        CREATE TABLE a_lines (order_id INTEGER REFERENCES m_orders(id), line INTEGER, qty INTEGER CHECK (qty > 0), PRIMARY KEY (order_id, line));
        CREATE INDEX ix_orders_placed ON m_orders (placed);
        """;

    private static readonly string[] Tables = ["z_customers", "m_orders", "a_lines"];


    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task StructureDumpOfAForeignKeyChainLoadsIntoAnEmptyDatabase(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture);
        var path = Path.Combine(DumpDirectory, "dump.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureOnly, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.Equal(path, outcome.FinalPath);
        var script = await File.ReadAllTextAsync(path);
        var positions = Tables.Select(t => script.IndexOf($"\n-- Table main.{t}\nCREATE TABLE ", StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p > 0, script));
        Assert.Equal(positions.Order(), positions);

        var target = await SessionAsync(provider, "target", null);
        await ExecuteScriptAsync(target, script);
        foreach (var table in Tables)
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(target, table));
    }

    [Fact]
    public async Task HeaderStatesSourceContentsAndOnlyTheReferencedTablesTheDumpDoesNotCreate()
    {
        var source = await SessionAsync("DuckDB", "header", Fixture);
        var path = Path.Combine(DumpDirectory, "header.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source,
            DumpSelection.ForTables(Table("a_lines"), Table("m_orders")), DumpMode.StructureOnly, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        var header = script[..script.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');
        Assert.Equal("-- DbConnect SQL dump (structure only)", header[0]);
        Assert.Equal("-- Provider: DuckDB", header[1]);
        Assert.Equal("-- Server: embedded (local file)", header[2]);
        Assert.Equal("-- Database: header", header[3]);
        Assert.Matches(@"^-- Created: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC$", header[4]);
        Assert.Equal("-- Includes: 2 tables, indexes", header[5]);
        Assert.Equal("-- Excludes: table data; views, routines and other objects outside these tables; users, roles and grants", header[6]);
        Assert.Equal("-- Prerequisites (must already exist; not scripted):", header[7]);
        Assert.Equal("--   Referenced table \"main\".\"z_customers\" (foreign key target)", header[8]);
        Assert.Equal(9, header.Length);
        Assert.True(script.IndexOf("-- Table main.m_orders", StringComparison.Ordinal) < script.IndexOf("-- Table main.a_lines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SchemaSelectionDumpsOnlyThatSchemasTables()
    {
        var source = await SessionAsync("DuckDB", "schemas", Fixture + "CREATE SCHEMA sales; CREATE TABLE sales.region (id INTEGER PRIMARY KEY);");
        var path = Path.Combine(DumpDirectory, "schema.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.ForSchemas("sales"), DumpMode.StructureOnly, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        Assert.Contains("-- Table sales.region\n", script);
        Assert.DoesNotContain("z_customers", script);
    }

    [Fact]
    public async Task ATableThatCannotBeScriptedFailsTheDumpNamingItAndPublishesNothing()
    {
        var source = await SessionAsync("SQLite", "missing", Fixture);
        var path = Path.Combine(DumpDirectory, "out", "missing.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source,
            DumpSelection.ForTables(Table("z_customers"), Table("gone")), DumpMode.StructureOnly, path);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.StartsWith("Table main.gone could not be scripted", outcome.Diagnostic);
        Assert.EndsWith("Destination was not published.", outcome.Diagnostic);
        Assert.Null(outcome.FinalPath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task CancelLeavesNoPublishedFile()
    {
        var source = await SessionAsync("SQLite", "cancel", Fixture);
        var path = Path.Combine(DumpDirectory, "out", "cancel.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterFirstTable(cancellation);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureOnly, path,
            progress: progress, cancellationToken: cancellation.Token);

        Assert.Equal(ExportStatus.Canceled, outcome.Status);
        Assert.Equal(new DumpProgress(1, 3, "main.a_lines"), progress.First);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    private sealed class CancelAfterFirstTable(CancellationTokenSource cancellation) : IProgress<DumpProgress>
    {
        public DumpProgress? First { get; private set; }
        public void Report(DumpProgress value)
        {
            First ??= value;
            cancellation.Cancel();
        }
    }
}
