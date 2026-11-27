using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>EXPLAIN runs on the query tab's own session, so state set on that session applies to the plan.</summary>
public sealed class ExplainOnTabSessionTests : MainWindowTestBase
{
    [Fact]
    public async Task Explain_SeesSessionScopedObjectsCreatedInTheTab()
    {
        var dbService = new DatabaseService();
        var connection = dbService.CreateSession(new ConnectionInfo
        {
            Name = "Local",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        });
        var vm = CreateMainWindowViewModel(dbService);
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(connection);
        tab.SelectedSession = connection;

        tab.QueryText = "CREATE TEMP TABLE plan_probe (id INTEGER);";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        tab.QueryText = "SELECT * FROM plan_probe;";
        await vm.ExplainQueryCommand.ExecuteAsync(null);

        Assert.NotNull(tab.ExplainResult);
        Assert.False(tab.ExplainResult!.HasError, tab.ExplainResult.Error?.ToString());
        Assert.True(tab.ShowExplainPane);
    }
}
