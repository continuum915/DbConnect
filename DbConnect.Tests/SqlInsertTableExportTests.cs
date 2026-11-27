using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>SQL INSERT for table sources: the table data tab and Export Data, with an optional CREATE TABLE (sql-dump-and-insert-export/03).</summary>
public sealed class SqlInsertTableExportTests : IDisposable
{
    private const string Fixture = """
        CREATE TABLE "order items" (id INTEGER PRIMARY KEY, name TEXT, qty INTEGER NOT NULL DEFAULT 1);
        INSERT INTO "order items" VALUES (1, 'it''s', 2), (2, 'b', 3), (3, NULL, 4);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-sqlinsert-table-{Guid.NewGuid():N}");
    public SqlInsertTableExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task ExportDataWithCreateTableReproducesTheTableInAnEmptyDatabase(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture.Replace("NULL, 4", "'c', 4"));
        var dialog = new ExportDataViewModel(source, [Choice(source, "missing"), Choice(source, "order items")])
        {
            Format = ExportFormat.SqlInsert,
            Folder = _directory
        };
        dialog.Options.IncludeCreateTable = true;
        dialog.Options.RowsPerStatement = 2;

        await dialog.ExportCommand.ExecuteAsync(null);

        var file = Path.Combine(_directory, "main.order items.sql");
        Assert.True(File.Exists(file), dialog.Tables[1].Result);
        Assert.StartsWith("Failed", dialog.Tables[0].Result);
        Assert.Equal("Exported 1 of 2 tables; 1 failed or canceled.", dialog.Summary);
        var target = await SessionAsync(provider, "target", null);
        await ExecuteScriptAsync(target, await File.ReadAllTextAsync(file));
        var copy = await QueryAsync(target, "SELECT id, name, qty FROM \"order items\" ORDER BY id");
        Assert.Equal([[1L, "it's", 2L], [2L, "b", 3L], [3L, "c", 4L]],
            copy.ObjectRows.Select(r => r.Select(v => v is int or long ? Convert.ToInt64(v) : v).ToArray()));
    }

    [Theory]
    [InlineData(ExportScope.CurrentTablePage)]
    [InlineData(ExportScope.AllMatchingTableRows)]
    public async Task TableTabTargetsTheSourceTableAndCanPrependItsCreateScript(ExportScope scope)
    {
        var source = await SessionAsync("SQLite", "tab", Fixture);
        var node = new SchemaNode { Name = "order items", RawName = "order items", Type = SchemaNodeType.Table, SchemaName = "main" };
        var tab = new TableDataTabViewModel("order items", source, node);
        await tab.RefreshInternalAsync(true);
        var path = Path.Combine(_directory, "tab.sql");
        tab.ExportPathPicker = new Picker(path);
        tab.ExportScope = scope;
        tab.ExportOptions.Format = ExportFormat.SqlInsert;
        tab.ExportOptions.IncludeCreateTable = true;

        await tab.ExportCommand.ExecuteAsync(null);
        await (await tab.GetOrOpenDbSessionAsync()).DisposeAsync();

        Assert.True(tab.ExportState.Outcome?.Status == ExportStatus.Completed, tab.ExportState.Message);
        Assert.Equal(string.Empty, tab.ExportOptions.SqlTableName);
        var script = await File.ReadAllTextAsync(path);
        Assert.StartsWith("-- SQLite table main.order items\n", script);
        Assert.Contains("\n\nINSERT INTO \"main\".\"order items\" (\"id\", \"name\", \"qty\") VALUES (1, 'it''s', 2);\n", script);
        Assert.EndsWith("INSERT INTO \"main\".\"order items\" (\"id\", \"name\", \"qty\") VALUES (3, NULL, 4);\n", script);
        var target = await SessionAsync("SQLite", "tab-target", null);
        await ExecuteScriptAsync(target, script);
        Assert.Equal(3L, (await QueryAsync(target, "SELECT count(*) FROM \"order items\" WHERE qty > 1")).ObjectRows[0][0]);
    }

    [Fact]
    public async Task TableTabRefusesCreateTableForADifferentTarget()
    {
        var source = await SessionAsync("SQLite", "rename", Fixture);
        var node = new SchemaNode { Name = "order items", RawName = "order items", Type = SchemaNodeType.Table, SchemaName = "main" };
        var tab = new TableDataTabViewModel("order items", source, node);
        await tab.RefreshInternalAsync(true);
        var path = Path.Combine(_directory, "rename.sql");
        tab.ExportPathPicker = new Picker(path);
        tab.ExportOptions.Format = ExportFormat.SqlInsert;
        tab.ExportOptions.SqlTableName = "copy_t";
        tab.ExportOptions.IncludeCreateTable = true;

        await tab.ExportCommand.ExecuteAsync(null);
        await (await tab.GetOrOpenDbSessionAsync()).DisposeAsync();

        Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome?.Status);
        Assert.Contains("CREATE TABLE", tab.ExportState.Message);
        Assert.False(File.Exists(path));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TableTabOffersSqlInsertOptionsIncludingCreateTable()
    {
        var tab = new TableDataTabViewModel { ShowExportPanel = true };
        var view = new DbConnect.Views.TableDataTabView { DataContext = tab };
        var create = view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertCreateTable")!;
        Assert.Contains(ExportFormat.SqlInsert, TableDataTabViewModel.ExportFormatOptions);
        Assert.False(create.IsVisible);

        tab.ExportOptions.Format = ExportFormat.SqlInsert;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(create.IsVisible);
        Assert.True(view.FindControl<Avalonia.Controls.TextBox>("SqlInsertTableName")!.IsVisible);
        Assert.True(view.FindControl<Avalonia.Controls.NumericUpDown>("SqlInsertRowsPerStatement")!.IsVisible);
        Assert.True(view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertColumnList")!.IsVisible);
        Assert.False(view.FindControl<Avalonia.Controls.ComboBox>("ExportDelimiterPicker")!.IsVisible);

        create.IsChecked = true;
        Assert.True(tab.ExportOptions.IncludeCreateTable);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void QueryResultsDoNotOfferCreateTable()
    {
        var tab = new QueryTabViewModel { ShowExportOptionsPanel = true };
        var view = new DbConnect.Views.QueryTabView { DataContext = tab };
        tab.ExportOptions.Format = ExportFormat.SqlInsert;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Avalonia.Controls.TextBox>("SqlInsertTableName")!.IsVisible);
        Assert.Null(view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertCreateTable"));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ExportDataOffersSqlInsertOptionsButNotATargetName()
    {
        var session = Session("SQLite", "dialog");
        var dialog = new ExportDataViewModel(session, []);
        var view = new DbConnect.Views.ExportDataDialog { DataContext = dialog };
        var create = view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertCreateTable")!;
        var rows = view.FindControl<Avalonia.Controls.NumericUpDown>("SqlInsertRowsPerStatement")!;
        Assert.Contains(ExportFormat.SqlInsert, ExportDataViewModel.Formats);
        Assert.False(create.IsEffectivelyVisible || rows.IsEffectivelyVisible);

        dialog.Format = ExportFormat.SqlInsert;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(create.IsEffectivelyVisible && rows.IsEffectivelyVisible && view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertColumnList")!.IsEffectivelyVisible);
        Assert.Null(view.FindControl<Avalonia.Controls.TextBox>("SqlInsertTableName"));

        rows.Value = 100;
        create.IsChecked = true;
        Assert.Equal(100, dialog.Options.RowsPerStatement);
        Assert.True(dialog.Options.IncludeCreateTable);
        view.Close();
    }

    [Fact]
    public async Task CreateTableScriptingErrorFailsTheExportWithTheReasonAndPublishesNothing()
    {
        var path = Path.Combine(_directory, "t.sql");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new ExportColumn("id", 0)], [[1L]]),
            new()
            {
                Format = ExportFormat.SqlInsert,
                SqlTableName = "t",
                IncludeCreateTable = true,
                CreateTableScript = _ => Task.FromResult(TableScriptResult.Fail("Check constraint ck is unreadable."))
            },
            ExportScope.AllMatchingTableRows);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("Check constraint ck is unreadable.", outcome.Diagnostic);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
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

    private static ExportTableChoice Choice(DatabaseSession session, string table) =>
        new(table, session.Objects.Bind(new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table, SchemaName = "main" }, null), true);

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
