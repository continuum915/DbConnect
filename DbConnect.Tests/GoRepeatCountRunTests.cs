using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class GoRepeatCountRunTests : MainWindowTestBase
{
    [Fact]
    public async Task AScriptOverTheCommandLimitIsRefusedBeforeConnecting()
    {
        var main = CreateMainWindowViewModel(new DatabaseService());
        var tab = main.NewQueryTabWithSession();
        var provider = new SqlServerProvider();
        var session = new DatabaseSession(new ConnectionInfo { Provider = provider.ProviderName, ConnectionString = "Server=tcp:invalid.invalid;Connect Timeout=1" }, provider);
        main.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT 1\nGO 2147483647";

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Contains("GO count", tab.CurrentResult.ErrorMessage);
        Assert.False(tab.IsExecuting);
    }
}
