using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Export Data ▸ SQL INSERT into one combined file, tables in foreign-key order (sql-dump-and-insert-export/08).</summary>
public sealed class ExportDataCombinedFileTests : IDisposable
{
    // Created children first, so catalog order is not dependency order; "audit" references a table left unticked.
    // The line break is written out, so the value does not depend on the checkout's line endings.
    private const string Fixture = $"""
        CREATE TABLE parent (id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id), note TEXT);
        CREATE TABLE audit (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
        INSERT INTO parent VALUES (1, 'it''s'), (2, 'b');
        INSERT INTO child VALUES (10, 1, 'x'), (11, 2, NULL), (12, 2, 'line{"\n"}break');
        INSERT INTO audit VALUES (100, 1);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-combined-{Guid.NewGuid():N}");
    public ExportDataCombinedFileTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task CombinedFileWritesTickedTablesParentsFirstAndLoadsIntoAnEmptyDatabase(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture);
        var dialog = new ExportDataViewModel(source, [Choice(source, "child", true), Choice(source, "audit", false), Choice(source, "parent", true)])
        {
            Format = ExportFormat.SqlInsert,
            Folder = _directory,
            CombinedFile = true
        };
        dialog.Options.IncludeCreateTable = true;
        dialog.Options.RowsPerStatement = 2;

        await dialog.ExportCommand.ExecuteAsync(null);

        var file = Path.Combine(_directory, dialog.CombinedFileName);
        Assert.True(File.Exists(file), dialog.Summary);
        Assert.Equal(["source.sql"], Directory.GetFiles(_directory, "*.sql").Select(Path.GetFileName));
        Assert.StartsWith("Exported 2 tables · 5 rows → source.sql", dialog.Summary);
        Assert.Equal("3 rows", dialog.Tables[0].Result);
        Assert.Equal("", dialog.Tables[1].Result);
        Assert.Equal("2 rows", dialog.Tables[2].Result);
        var script = await File.ReadAllTextAsync(file);
        Assert.True(script.IndexOf("-- Table main.parent\n", StringComparison.Ordinal) < script.IndexOf("-- Table main.child\n", StringComparison.Ordinal),
            script);
        Assert.Contains("-- Includes: 2 tables (main.parent, main.child), CREATE TABLE, rows as INSERT statements\n", script);
        Assert.DoesNotContain("audit", script);

        var target = await SessionAsync(provider, "target", null);
        await ExecuteScriptAsync(target, script);
        var rows = await QueryAsync(target, "SELECT c.id, p.name, c.note FROM child c JOIN parent p ON p.id = c.parent_id ORDER BY c.id");
        Assert.Equal([[10L, "it's", "x"], [11L, "b", null], [12L, "b", "line\nbreak"]],
            rows.ObjectRows.Select(r => r.Select(v => v is int or long ? Convert.ToInt64(v) : v).ToArray()));
    }

    [Fact]
    public async Task HeaderListsReferencedTablesThatWereNotTickedAsPrerequisites()
    {
        var source = await SessionAsync("SQLite", "prereq", Fixture);
        var dialog = new ExportDataViewModel(source, [Choice(source, "child", true), Choice(source, "parent", false)])
        {
            Format = ExportFormat.SqlInsert,
            Folder = _directory,
            CombinedFile = true
        };

        await dialog.ExportCommand.ExecuteAsync(null);

        var script = await File.ReadAllTextAsync(Path.Combine(_directory, "prereq.sql"));
        Assert.Contains("-- Prerequisites (must already exist; not scripted):\n--   Referenced table parent (foreign key target)\n", script);
        Assert.Contains("-- Includes: 1 table (main.child), rows as INSERT statements\n", script);
    }

    [Fact]
    public async Task ATableThatFailsFailsTheWholeFileAndPublishesNothing()
    {
        var source = await SessionAsync("SQLite", "failing", Fixture);
        var dialog = new ExportDataViewModel(source, [Choice(source, "parent", true), Choice(source, "missing", true)])
        {
            Format = ExportFormat.SqlInsert,
            Folder = _directory,
            CombinedFile = true
        };

        await dialog.ExportCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(_directory, "*.sql"));
        Assert.Empty(Directory.GetFiles(_directory, ".dbconnect-export-*"));
        Assert.StartsWith("Failed", dialog.Tables[1].Result);
        Assert.Contains("main.missing", dialog.Summary);
        Assert.Contains("was not published", dialog.Summary);
    }

    [Fact]
    public async Task CancelLeavesNoPublishedFile()
    {
        var source = await SessionAsync("SQLite", "cancel", Fixture +
            "INSERT INTO parent WITH RECURSIVE s(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM s WHERE value < 20000) SELECT value + 10, 'n' FROM s;");
        var dialog = new ExportDataViewModel(source, [Choice(source, "parent", true), Choice(source, "child", true)])
        {
            Format = ExportFormat.SqlInsert,
            Folder = _directory,
            CombinedFile = true
        };
        // Cancel once the first table has written rows to the temporary file.
        dialog.Tables[0].PropertyChanged += (_, _) =>
        {
            if (dialog.Tables[0].Result == "Exporting… 1000 rows") dialog.CancelCommand.Execute(null);
        };

        await dialog.ExportCommand.ExecuteAsync(null);

        Assert.StartsWith("Canceled", dialog.Summary);
        Assert.Equal("Not exported (canceled)", dialog.Tables[1].Result);
        Assert.Empty(Directory.GetFiles(_directory, "*.sql"));
        Assert.Empty(Directory.GetFiles(_directory, ".dbconnect-export-*"));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void CombinedFileIsOfferedForSqlInsertOnly()
    {
        var dialog = new ExportDataViewModel(Session("SQLite", "view"), []);
        var view = new DbConnect.Views.ExportDataDialog { DataContext = dialog };
        var combined = view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertCombinedFile")!;
        Assert.False(combined.IsEffectivelyVisible);

        dialog.Format = ExportFormat.SqlInsert;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(combined.IsEffectivelyVisible);
        combined.IsChecked = true;
        Assert.True(dialog.CombinedFile);
        Assert.Equal("view.sql", dialog.CombinedFileName);

        dialog.Format = ExportFormat.Csv;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(combined.IsEffectivelyVisible);
        Assert.False(dialog.WritesCombinedFile);
        view.Close();
    }

    private DatabaseSession Session(string provider, string name) => new DatabaseService().CreateSession(new ConnectionInfo
    {
        Name = name,
        Provider = provider,
        ConnectionString = $"Data Source={Path.Combine(_directory, name + (provider == "DuckDB" ? ".duckdb" : ".sqlite"))}"
    });

    private async Task<DatabaseSession> SessionAsync(string provider, string name, string? setup)
    {
        var session = Session(provider, name);
        if (setup != null) await ExecuteScriptAsync(session, setup);
        return session;
    }

    private static ExportTableChoice Choice(DatabaseSession session, string table, bool ticked) =>
        new(table, session.Objects.Bind(new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table, SchemaName = "main" }, null), ticked);

    private static async Task ExecuteScriptAsync(DatabaseSession session, string script)
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        await SqlInsertRoundTrip.ExecuteAsync(db, script);
    }

    private static async Task<QueryResult> QueryAsync(DatabaseSession session, string sql)
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        return await SqlInsertRoundTrip.QueryAsync(db, sql);
    }
}
