using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-16 — A query tab's result grid is editable for a single keyed table, read-only with a reason otherwise.</summary>
public sealed class QueryTabEditableResultsTests : MainWindowTestBase, IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"rain-tab-edit-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databaseFile, _databaseFile + "-journal" })
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private async Task<QueryTabViewModel> RunAsync(string sql) => (await OpenTabAsync(sql)).Tab;

    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab)> OpenTabAsync(string sql)
    {
        var service = new DatabaseService();
        var session = service.CreateSession(new ConnectionInfo
        {
            Name = "tab-edit",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        await session.Provider.ExecuteNonQueryAsync(session.ConnectionString, """
            CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, email TEXT);
            CREATE TABLE IF NOT EXISTS orders (id INTEGER PRIMARY KEY, customer_id INTEGER, status TEXT);
            DELETE FROM orders; DELETE FROM customers;
            INSERT INTO customers VALUES (1, 'ada@example.com');
            INSERT INTO orders VALUES (1, 1, 'new'), (2, 1, 'new'), (12, 1, 'new');
            """);
        var vm = CreateMainWindowViewModel(service);
        vm.ActiveSessionOptions.Add(session);
        var tab = vm.NewQueryTabWithSession(session);
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        await tab.ResultEditor.PendingLoad;
        return (vm, tab);
    }

    /// <summary>Opens an editable result and edits one cell, leaving it pending.</summary>
    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab)> OpenTabWithPendingEditAsync()
    {
        var (vm, tab) = await OpenTabAsync("SELECT * FROM orders WHERE id < 10");
        var editor = tab.ResultEditor;
        Assert.Null(editor.SetCellText(editor.RowFor(tab.DisplayResultRows[0])!, 2, "pending"));
        return (vm, tab);
    }

    [Fact]
    public async Task Copy_WritesPendingEdits_NotTheOriginalValues()
    {
        var (_, tab) = await OpenTabWithPendingEditAsync();
        var grid = tab.ResultGrid;
        grid.IncludeHeader = false;
        grid.SqlLiterals = true;

        Assert.Equal("'pending'", grid.BuildCellText(tab.DisplayResultRows[0], 2));
        Assert.StartsWith("1\t1\t'pending'", grid.BuildRowsText(tab.DisplayResultRows));
    }

    [Fact]
    public async Task RunningAnotherQuery_WithPendingEdits_AsksFirst_AndDecliningKeepsThem()
    {
        var (vm, tab) = await OpenTabWithPendingEditAsync();
        var before = tab.CurrentResult;
        tab.ResultEditor.DiscardConfirmation = _ => Task.FromResult(false);

        tab.QueryText = "SELECT 1";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Same(before, tab.CurrentResult);
        Assert.True(tab.ResultEditor.HasChanges);
    }

    [Fact]
    public async Task RunningAnotherQuery_AfterAgreeingToDiscard_Runs()
    {
        var (vm, tab) = await OpenTabWithPendingEditAsync();
        var before = tab.CurrentResult;
        tab.ResultEditor.DiscardConfirmation = _ => Task.FromResult(true);

        tab.QueryText = "SELECT 1";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.NotSame(before, tab.CurrentResult);
        Assert.False(tab.ResultEditor.HasChanges);
    }

    [Fact]
    public async Task ClosingTheTab_WithPendingEdits_AsksFirst()
    {
        var (vm, tab) = await OpenTabWithPendingEditAsync();
        tab.ResultEditor.DiscardConfirmation = _ => Task.FromResult(false);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Contains(tab, vm.Tabs);
        Assert.True(tab.ResultEditor.HasChanges);
    }

    [Fact]
    public async Task ChangingConnection_WithPendingEdits_IsRefused()
    {
        var (vm, tab) = await OpenTabWithPendingEditAsync();
        var original = tab.SelectedSession;
        var other = AddOtherConnection(vm);

        Assert.False(tab.CanChangeConnection);
        tab.SelectedSession = other;

        Assert.Same(original, tab.SelectedSession);
        Assert.True(tab.ResultEditor.HasChanges);
    }

    [Fact]
    public async Task ResultAfterChangingConnection_IsReadOnly_WithAReason()
    {
        var (vm, tab) = await OpenTabAsync("SELECT * FROM orders WHERE id < 10");
        Assert.True(tab.ResultEditor.IsEditable);

        tab.SelectedSession = AddOtherConnection(vm);
        await tab.ResultEditor.PendingLoad;

        Assert.False(tab.ResultEditor.IsEditable);
        Assert.Contains("connection", tab.ResultEditor.ReadOnlyReason);
    }

    private DatabaseSession AddOtherConnection(MainWindowViewModel vm)
    {
        var other = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "other",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        vm.ActiveSessionOptions.Add(other);
        return other;
    }

    [Fact]
    public async Task SelectFromOneKeyedTable_IsEditable_AndCommitsOnTheTabsSession()
    {
        var tab = await RunAsync("SELECT * FROM orders WHERE id < 10");
        var editor = tab.ResultEditor;
        Assert.True(editor.IsEditable);

        Assert.Equal("new", tab.DisplayResultRows.Single(r => r[0] == "2")[2]); // formatted (and cached) before the edit
        var row = editor.RowFor(tab.DisplayResultRows.Single(r => r[0] == "2"))!;
        editor.SetCellText(row, 2, "shipped");
        await editor.ExecuteCommitAsync();

        var check = await (await tab.GetOrOpenDbSessionAsync()).ExecuteQueryAsync("SELECT status FROM orders WHERE id = 2");
        Assert.Equal("shipped", check.ObjectRows.Single()[0]);

        // Copy, filter and sort read the tab's display rows, which must show the saved value.
        await editor.PendingLoad;
        Assert.Equal("shipped", tab.DisplayResultRows.Single(r => r[0] == "2")[2]);
        Assert.True(editor.IsEditable);
    }

    [Fact]
    public async Task JoinResult_IsReadOnly_WithAReason()
    {
        var tab = await RunAsync("SELECT o.id, c.email FROM orders o JOIN customers c ON c.id = o.customer_id");

        Assert.False(tab.ResultEditor.IsEditable);
        Assert.Contains("customers", tab.ResultEditor.ReadOnlyReason);
    }

    [Fact]
    public async Task ResultMissingTheKey_IsReadOnly_WithAReason()
    {
        var tab = await RunAsync("SELECT status FROM orders");

        Assert.False(tab.ResultEditor.IsEditable);
        Assert.Contains("id", tab.ResultEditor.ReadOnlyReason);
    }
}
