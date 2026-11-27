using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Fetching past the row cap from the still-open reader (Tier 2 §2.8).</summary>
[Collection("AppPreferences")]
public sealed class LargeQueryResultTests : MainWindowTestBase, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rain-paging-" + Guid.NewGuid().ToString("N") + ".db");

    public LargeQueryResultTests()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2500)
            INSERT INTO items SELECT i, 'row ' || i FROM n;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        AppPreferences.Reset();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private (MainWindowViewModel Vm, QueryTabViewModel Tab) Tab()
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        vm.Tabs.Clear();
        var tab = new QueryTabViewModel();
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        var session = new DatabaseSession(new ConnectionInfo { Name = "paging", Provider = "SQLite", ConnectionString = $"Data Source={_path};Pooling=False" }, new SqliteProvider());
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.ResultRowCap = 1000;
        return (vm, tab);
    }

    private static async Task Run(MainWindowViewModel vm, QueryTabViewModel tab, string sql)
    {
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task CappedResult_FetchesNextPages_ThenAllRemainingRows()
    {
        var (vm, tab) = Tab();
        await Run(vm, tab, "SELECT id, label FROM items ORDER BY id");
        Assert.Equal(1000, tab.CurrentResult!.ObjectRows.Count);
        Assert.True(tab.Paging.HasMore);
        Assert.Contains("more available", tab.Paging.Summary);

        await tab.Paging.FetchNextCommand.ExecuteAsync(null);
        Assert.Equal(2000, tab.CurrentResult.ObjectRows.Count);
        Assert.Equal(2000, tab.DisplayResultRows.Count);

        await tab.Paging.FetchAllCommand.ExecuteAsync(null);
        var rows = tab.CurrentResult.ObjectRows;
        Assert.Equal(2500, rows.Count);
        Assert.Equal(Enumerable.Range(1, 2500).Select(i => (long)i), rows.Select(r => Convert.ToInt64(r[0])));
        Assert.False(tab.Paging.HasMore);
        Assert.False(tab.CurrentResult.IsTruncated);
        Assert.Contains("All 2,500 rows loaded", tab.Paging.Summary);
        await tab.ReleaseDbSessionAsync();
    }

    [Fact]
    public async Task AnotherRun_CommitAndRelease_CloseTheOpenResultWithoutBlocking()
    {
        var (vm, tab) = Tab();
        tab.IsManualCommit = true;
        await Run(vm, tab, "SELECT id FROM items");
        Assert.True(tab.Paging.HasMore);
        Assert.True(tab.IsInTransaction);

        await Run(vm, tab, "SELECT count(*) FROM items").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2500L, tab.CurrentResult!.ObjectRows[0][0]);
        Assert.False(tab.Paging.HasMore);

        await Run(vm, tab, "SELECT id FROM items");
        Assert.True(tab.Paging.HasMore);
        await tab.CommitCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(tab.IsInTransaction);

        await Run(vm, tab, "SELECT id FROM items");
        await tab.ReleaseDbSessionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await tab.Paging.FetchNextCommand.ExecuteAsync(null);
        Assert.False(tab.Paging.HasMore);
    }

    [Fact]
    public async Task FetchAll_StopsAtThePreferencesRowLimit()
    {
        AppPreferences.Apply(new AppSettings { Results = new ResultPreferences { MaxResultRows = 1500 } });
        var (vm, tab) = Tab();
        await Run(vm, tab, "SELECT id FROM items");
        await tab.Paging.FetchAllCommand.ExecuteAsync(null);
        Assert.Equal(1500, tab.CurrentResult!.ObjectRows.Count);
        Assert.Contains("1,500-row limit", tab.Paging.Summary);
        await tab.ReleaseDbSessionAsync();
    }

    [Fact]
    public async Task UnsavedEdits_BlockFetching()
    {
        var (vm, tab) = Tab();
        await Run(vm, tab, "SELECT id, label FROM items");
        Assert.True(tab.ResultEditor.IsEditable, tab.ResultEditor.ReadOnlyReason);
        var row = tab.ResultEditor.RowFor(tab.DisplayResultRows[0])!;
        tab.ResultEditor.SetCellText(row, 1, "changed");
        await tab.Paging.FetchNextCommand.ExecuteAsync(null);
        Assert.Equal(1000, tab.CurrentResult!.ObjectRows.Count);
        Assert.Contains("Commit or revert", tab.Paging.Summary);
        await tab.ReleaseDbSessionAsync();
    }

    [Fact]
    public async Task ResultUnderTheCap_HasNothingToFetch()
    {
        var (vm, tab) = Tab();
        await Run(vm, tab, "SELECT id FROM items WHERE id <= 10");
        Assert.False(tab.Paging.HasMore);
        Assert.Equal(string.Empty, tab.Paging.Summary);
        await tab.ReleaseDbSessionAsync();
    }
}

public sealed class LargeQueryResultHeadlessTests : MainWindowTestBase
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ScrollingTheGridToTheEndFetchesTheNextPage()
    {
        var path = Path.Combine(Path.GetTempPath(), "rain-scroll-" + Guid.NewGuid().ToString("N") + ".db");
        await new SqliteProvider().ExecuteNonQueryAsync($"Data Source={path}",
            "CREATE TABLE t (i INTEGER); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300) INSERT INTO t SELECT i FROM n;");
        var main = CreateMainWindowViewModel();
        // A tab without the workspace store: its saved row cap would otherwise replace the one set here.
        main.Tabs.Clear();
        var tab = new QueryTabViewModel();
        main.Tabs.Add(tab);
        main.SelectedTab = tab;
        var session = new DatabaseSession(new ConnectionInfo { Name = "scroll", Provider = "SQLite", ConnectionString = $"Data Source={path};Pooling=False" }, new SqliteProvider());
        main.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.ResultRowCap = 100;
        tab.QueryText = "SELECT i FROM t";
        var view = new DbConnect.Views.QueryTabView { DataContext = tab };
        var window = new Avalonia.Controls.Window { Content = view, Width = 900, Height = 600 };
        try
        {
            window.Show();
            await main.ExecuteQueryCommand.ExecuteAsync(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(tab.Paging.HasMore);
            var grid = view.FindControl<Avalonia.Controls.DataGrid>("ResultsGrid")!;
            var bar = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(grid)
                .OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(b => b.Name == "PART_VerticalScrollbar");
            bar.Value = bar.Maximum;
            for (var i = 0; i < 250 && (tab.CurrentResult!.ObjectRows.Count == 100 || tab.Paging.IsFetching); i++)
            {
                await Task.Delay(20);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            // Let any layout pass caused by the rebuild settle; it must not start a second fetch.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(20);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(200, tab.CurrentResult!.ObjectRows.Count);
        }
        finally
        {
            window.Close();
            await tab.ReleaseDbSessionAsync();
            view.DataContext = null;
            session.Catalog.Dispose();
        }
    }
}
