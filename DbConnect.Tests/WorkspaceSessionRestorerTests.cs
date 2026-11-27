using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class WorkspaceSessionRestorerTests : MainWindowTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUncleanSession_KeepsTheCurrentWorkspace(bool missing)
    {
        var workspace = CreateMainWindowViewModel();
        var before = workspace.Tabs.ToArray();
        var selected = workspace.SelectedTab;

        WorkspaceSessionRestorer.Restore(workspace, missing ? null : new SavedSession
        {
            Tabs = [new SavedTab { QueryText = "select 99;" }],
            WasCleanShutdown = false
        });

        Assert.Equal(before, workspace.Tabs);
        Assert.Same(selected, workspace.SelectedTab);
    }

    [Fact]
    public void CleanSession_RestoresQueryContentAndSelection()
    {
        var workspace = CreateMainWindowViewModel();
        Assert.True(workspace.HasNoTabs);

        WorkspaceSessionRestorer.Restore(workspace, new SavedSession
        {
            WasCleanShutdown = true,
            SelectedTabIndex = 1,
            Tabs = [new SavedTab { QueryText = "select 1;", Title = "First" }, new SavedTab { Title = " " }]
        });

        Assert.Equal(2, workspace.Tabs.Count);
        var first = Assert.IsType<QueryTabViewModel>(workspace.Tabs[0]);
        Assert.Equal("select 1;", first.QueryText);
        Assert.Equal("First", first.Title);
        var second = Assert.IsType<QueryTabViewModel>(workspace.SelectedTab);
        Assert.Same(workspace.Tabs[1], second);
        Assert.Equal("Query", second.Title);
        Assert.Equal(string.Empty, second.QueryText);
    }

    [Fact]
    public void RestoredTabs_MatchConnectionIdAndSkipUnavailableTables()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tabs.Clear();
        var connection = new ConnectionInfo { Name = "Renamed" };
        var session = new DatabaseSession(connection, new SqliteProvider());
        workspace.ActiveSessionOptions.Add(session);

        WorkspaceSessionRestorer.Restore(workspace, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [
                new SavedTab { ConnectionId = connection.Id, ConnectionName = "Old name", QueryText = "select 42;" },
                new SavedTab { TabType = "TableData", ConnectionId = connection.Id, TableName = "Customers" },
                new SavedTab { TabType = "TableData", ConnectionName = "Missing", TableName = "Other" }
            ]
        });

        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(session, Assert.IsType<QueryTabViewModel>(workspace.Tabs[0]).SelectedSession);
        Assert.Same(session, Assert.IsType<TableDataTabViewModel>(workspace.Tabs[1]).Session);
    }

    [Fact]
    public void RestoredQueryTab_WaitsForItsOwnConnection_AndKeepsItWhenSavedAgain()
    {
        var workspace = CreateMainWindowViewModel();
        var shop = new ConnectionInfo { Name = "Shop" };
        var other = new ConnectionInfo { Name = "Other" };

        WorkspaceSessionRestorer.Restore(workspace, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [
                new SavedTab { ConnectionId = shop.Id, ConnectionName = "Shop", QueryText = "select 1;" },
                new SavedTab { QueryText = "select 2;" }
            ]
        });

        var waiting = Assert.IsType<QueryTabViewModel>(workspace.Tabs[0]);
        var unbound = Assert.IsType<QueryTabViewModel>(workspace.Tabs[1]);
        Assert.Null(waiting.SelectedSession);
        Assert.Equal("Shop (connect in the explorer)", waiting.ConnectionPlaceholder);
        Assert.False(waiting.WaitingForConnection!.Matches(other));
        Assert.True(waiting.WaitingForConnection.Matches(shop));
        Assert.Null(unbound.WaitingForConnection);

        var resaved = SessionTabPersistence.Capture(waiting)!;
        Assert.Equal(shop.Id, resaved.ConnectionId);
        Assert.Equal("Shop", resaved.ConnectionName);
        Assert.Equal("No connection", unbound.ConnectionPlaceholder);
    }
}
