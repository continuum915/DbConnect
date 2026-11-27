using System;
using System.IO;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Per-tab manual-commit mode: toggle, pending-transaction indicator, commit/rollback, close prompt.</summary>
public sealed class QueryTabManualCommitTests : MainWindowTestBase, IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"rain-manual-commit-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        foreach (var path in new[] { _databaseFile, _databaseFile + "-journal", _databaseFile + "-wal", _databaseFile + "-shm", _databaseFile + ".other", _databaseFile + ".other-journal" })
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task NewTab_IsAutoCommit_AndDmlOpensNoTransaction()
    {
        var (vm, tab) = await CreateAsync();

        Assert.False(tab.IsManualCommit);
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        Assert.False(tab.IsInTransaction);
        Assert.Equal(string.Empty, tab.TransactionStatusText);
        Assert.Equal(1L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task ManualCommit_Dml_ShowsAPendingTransaction_AndHidesChangesFromOtherTabs()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;

        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        Assert.True(tab.IsInTransaction);
        Assert.NotEqual(string.Empty, tab.TransactionStatusText);
        Assert.Equal(0L, await CountAsync(vm, Other(vm, tab)));
        Assert.Equal(1L, await CountAsync(vm, tab));
    }

    [Fact]
    public async Task Commit_MakesChangesVisible_AndClosesTheTransaction()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        await tab.CommitCommand.ExecuteAsync(null);

        Assert.False(tab.IsInTransaction);
        Assert.Equal(1L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task Rollback_DiscardsChanges_AndClosesTheTransaction()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        await tab.RollbackCommand.ExecuteAsync(null);

        Assert.False(tab.IsInTransaction);
        Assert.Equal(0L, await CountAsync(vm, tab));
        Assert.Equal(0L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task ManualCommit_NextRunAfterCommit_StartsANewTransaction()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        await tab.CommitCommand.ExecuteAsync(null);

        await RunAsync(vm, tab, "INSERT INTO items VALUES (2);");

        Assert.True(tab.IsInTransaction);
        Assert.Equal(1L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task TypedCommit_EndsTheTransaction_AndTheTabKeepsWorking()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        await RunAsync(vm, tab, "COMMIT;");

        Assert.False(tab.IsInTransaction);
        Assert.Equal(1L, await CountAsync(vm, Other(vm, tab)));

        // The next statement starts a fresh transaction, and the Commit button works on it.
        await RunAsync(vm, tab, "INSERT INTO items VALUES (2);");
        Assert.True(tab.IsInTransaction);
        await tab.CommitCommand.ExecuteAsync(null);
        Assert.False(tab.IsInTransaction);
        Assert.Equal(2L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task TypedRollback_EndsTheTransaction_AndTheTabKeepsWorking()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        await RunAsync(vm, tab, "ROLLBACK;");

        Assert.False(tab.IsInTransaction);
        Assert.Equal(0L, await CountAsync(vm, tab));

        await RunAsync(vm, tab, "INSERT INTO items VALUES (2);");
        Assert.True(tab.IsInTransaction);
    }

    [Fact]
    public async Task CommitAndRollback_AreOnlyAvailableWhileATransactionIsOpen()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        Assert.False(tab.CommitCommand.CanExecute(null));
        Assert.False(tab.RollbackCommand.CanExecute(null));

        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        Assert.True(tab.CommitCommand.CanExecute(null));
        Assert.True(tab.RollbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task SwitchingToAutoCommit_IsRefusedWhileATransactionIsOpen()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        Assert.False(tab.CanChangeCommitMode);

        tab.IsManualCommit = false;

        Assert.True(tab.IsManualCommit);
        Assert.True(tab.IsInTransaction);
    }

    [Fact]
    public async Task SwitchingToAutoCommit_IsAllowedOnceTheTransactionEnds()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        await tab.CommitCommand.ExecuteAsync(null);
        Assert.True(tab.CanChangeCommitMode);

        tab.IsManualCommit = false;

        Assert.False(tab.IsManualCommit);
    }

    [Fact]
    public async Task ChangingTheConnection_IsRefusedWhileATransactionIsOpen()
    {
        var (vm, tab) = await CreateAsync();
        var original = tab.SelectedSession;
        var elsewhere = AddSecondConnection(vm);
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        Assert.False(tab.CanChangeConnection);

        tab.SelectedSession = elsewhere;

        Assert.Same(original, tab.SelectedSession);
        Assert.True(tab.IsInTransaction);
        await tab.CommitCommand.ExecuteAsync(null);
        Assert.Equal(1L, await CountAsync(vm, Other(vm, tab)));
    }

    [Fact]
    public async Task ChangingTheConnection_IsAllowedWhenNoTransactionIsOpen()
    {
        var (vm, tab) = await CreateAsync();
        var elsewhere = AddSecondConnection(vm);
        Assert.True(tab.CanChangeConnection);

        tab.SelectedSession = elsewhere;

        Assert.Same(elsewhere, tab.SelectedSession);
    }

    [Fact]
    public async Task ChangingTheConnection_IsAllowedOnceTheTransactionEnds()
    {
        var (vm, tab) = await CreateAsync();
        var elsewhere = AddSecondConnection(vm);
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        await tab.RollbackCommand.ExecuteAsync(null);

        tab.SelectedSession = elsewhere;

        Assert.Same(elsewhere, tab.SelectedSession);
    }

    [Fact]
    public async Task DisconnectingTheTabsConnection_StillSwitchesAwayAndDropsTheTransaction()
    {
        // The connection is going away, so there is nothing to keep the tab on.
        var (vm, tab) = await CreateAsync();
        var original = tab.SelectedSession!;
        var elsewhere = AddSecondConnection(vm);
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        vm.ActiveSessionOptions.Remove(original);

        tab.RemoveSession(original);

        Assert.Same(elsewhere, tab.SelectedSession);
        Assert.False(tab.IsInTransaction);
    }

    [Fact]
    public async Task StatusBar_ShowsThePendingTransactionOfTheSelectedTab()
    {
        var (vm, tab) = await CreateAsync();
        Assert.Equal(string.Empty, vm.StatusTransactionText);
        tab.IsManualCommit = true;

        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        Assert.NotEqual(string.Empty, vm.StatusTransactionText);

        vm.SelectedTab = Other(vm, tab);
        Assert.Equal(string.Empty, vm.StatusTransactionText);

        vm.SelectedTab = tab;
        await tab.RollbackCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.StatusTransactionText);
    }

    [Fact]
    public async Task ClosingATabWithAnOpenTransaction_AsksFirst_AndKeepsTheTabWhenDeclined()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        QueryTabViewModel? asked = null;
        vm.RequestTransactionCloseConfirmation = t => { asked = t; return Task.FromResult(false); };

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Same(tab, asked);
        Assert.Contains(tab, vm.Tabs);
        Assert.True(tab.IsInTransaction);
    }

    [Fact]
    public async Task ClosingATabWithAnOpenTransaction_WhenConfirmed_RollsBackAndCloses()
    {
        var (vm, tab) = await CreateAsync();
        var other = Other(vm, tab);
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        vm.RequestTransactionCloseConfirmation = _ => Task.FromResult(true);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.Equal(0L, await CountAsync(vm, other));
    }

    [Fact]
    public async Task ClosingASelectedTabWithAnOpenTransaction_AsksFirstToo()
    {
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");
        vm.SelectedTab = tab;
        var asked = 0;
        vm.RequestTransactionCloseConfirmation = _ => { asked++; return Task.FromResult(false); };

        await vm.CloseSelectedTabCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Contains(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingATabWithNoTransaction_DoesNotAsk()
    {
        var (vm, tab) = await CreateAsync();
        var asked = false;
        vm.RequestTransactionCloseConfirmation = _ => { asked = true; return Task.FromResult(false); };

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.False(asked);
        Assert.DoesNotContain(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingATabWithAnOpenTransaction_WithNoDialogAvailable_KeepsTheTab()
    {
        // Closing discards uncommitted work, so without a way to ask the answer is no.
        var (vm, tab) = await CreateAsync();
        tab.IsManualCommit = true;
        await RunAsync(vm, tab, "INSERT INTO items VALUES (1);");

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Contains(tab, vm.Tabs);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>A view model with two tabs on one SQLite file that already has an empty items table.</summary>
    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab)> CreateAsync()
    {
        var service = new DatabaseService();
        var session = service.CreateSession(new ConnectionInfo
        {
            Name = "manual-commit",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        var vm = CreateMainWindowViewModel(service);
        vm.ActiveSessionOptions.Add(session);
        var tab = vm.NewQueryTabWithSession(session);
        vm.NewQueryTabWithSession(session);

        await RunAsync(vm, tab, "CREATE TABLE items (id INTEGER);");
        return (vm, tab);
    }

    /// <summary>A second connection, to a different database file, available to every tab.</summary>
    private DatabaseSession AddSecondConnection(MainWindowViewModel vm)
    {
        var second = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "elsewhere",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}.other"
        });
        vm.ActiveSessionOptions.Add(second);
        return second;
    }

    private static QueryTabViewModel Other(MainWindowViewModel vm, QueryTabViewModel tab) =>
        vm.Tabs.OfType<QueryTabViewModel>().First(t => t.SelectedSession != null && !ReferenceEquals(t, tab));

    private static async Task RunAsync(MainWindowViewModel vm, QueryTabViewModel tab, string sql)
    {
        vm.SelectedTab = tab;
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.True(tab.CurrentResult?.HasError != true, $"[{sql}] {tab.CurrentResult?.Error?.Summary} {tab.CurrentResult?.Error?.Detail} {tab.CurrentResult?.ErrorMessage}");
    }

    private static async Task<long> CountAsync(MainWindowViewModel vm, QueryTabViewModel tab)
    {
        await RunAsync(vm, tab, "SELECT COUNT(*) AS n FROM items;");
        var result = tab.CurrentResult!;
        return (long)result.ObjectRows.Single()[result.IndexOfColumn("n")]!;
    }
}
