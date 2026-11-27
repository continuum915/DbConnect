using System;
using System.IO;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Each query tab owns one database session; tabs never share one.</summary>
public sealed class QueryTabDbSessionTests : MainWindowTestBase, IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"rain-tab-session-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        foreach (var path in new[] { _databaseFile, _databaseFile + "-journal", _databaseFile + "-wal", _databaseFile + "-shm" })
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TempTable_CreatedInOneRun_IsQueryableInTheNextRunOfTheSameTab()
    {
        var (vm, session) = CreateViewModel();
        var tab = vm.NewQueryTabWithSession(session);

        tab.QueryText = "CREATE TEMP TABLE scratch (value INTEGER); INSERT INTO scratch VALUES (7);";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.False(tab.CurrentResult?.HasError);

        tab.QueryText = "SELECT value FROM scratch;";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.False(tab.CurrentResult?.HasError);
        var result = tab.CurrentResult!;
        Assert.Equal(7L, result.ObjectRows.Single()[result.IndexOfColumn("value")]);
    }

    [Fact]
    public async Task TempTable_IsNotVisibleToAnotherTabOnTheSameConnection()
    {
        var (vm, session) = CreateViewModel();
        var first = vm.NewQueryTabWithSession(session);
        first.QueryText = "CREATE TEMP TABLE scratch (value INTEGER);";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.False(first.CurrentResult?.HasError);

        var second = vm.NewQueryTabWithSession(session);
        second.QueryText = "SELECT value FROM scratch;";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(second.CurrentResult?.HasError);
    }

    [Fact]
    public async Task ClosingTheTab_ReleasesItsSession()
    {
        var (vm, session) = CreateViewModel();
        var tab = vm.NewQueryTabWithSession(session);
        tab.QueryText = "SELECT 1;";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.True(tab.HasDbSession);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.False(tab.HasDbSession);
    }

    [Fact]
    public async Task ChangingTheTabConnection_ReleasesTheOldSession()
    {
        var (vm, session) = CreateViewModel();
        var other = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "other",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        vm.ActiveSessionOptions.Add(other);
        var tab = vm.NewQueryTabWithSession(session);
        tab.QueryText = "CREATE TEMP TABLE scratch (value INTEGER);";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.True(tab.HasDbSession);

        tab.SelectedSession = other;

        Assert.False(tab.HasDbSession);
    }

    private (MainWindowViewModel Vm, DatabaseSession Session) CreateViewModel()
    {
        var service = new DatabaseService();
        var session = service.CreateSession(new ConnectionInfo
        {
            Name = "tab-session",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        var vm = CreateMainWindowViewModel(service);
        vm.ActiveSessionOptions.Add(session);
        return (vm, session);
    }
}
