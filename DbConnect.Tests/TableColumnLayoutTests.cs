using System;
using System.IO;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-14 — hide/show, reorder and freeze in the table grid, remembered per connection and table.</summary>
public class TableColumnLayoutTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"rain-column-layout-{Guid.NewGuid():N}.json");
    private readonly Guid _connectionId = Guid.NewGuid();

    public void Dispose()
    {
        if (File.Exists(_workspacePath))
            File.Delete(_workspacePath);
    }

    private async Task<TableDataTabViewModel> OpenTabAsync(string table = "Customers")
    {
        var tableNode = CreateTableNode(hasPrimaryKey: true);
        tableNode.Name = tableNode.RawName = table;
        var tab = new TableDataTabViewModel(table, CreateLayoutSession(),
            new QueryWorkspaceService(_workspacePath), tableNode);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        return tab;
    }

    private DatabaseSession CreateLayoutSession()
    {
        var session = CreateSession(new TablePagingProvider(CreateTypedPage()));
        session.Connection.Id = _connectionId;
        return session;
    }

    [Fact]
    public async Task NewTab_ShowsAllColumnsInSourceOrderUnfrozen()
    {
        var tab = await OpenTabAsync();

        Assert.Equal(["Id", "Name", "IsActive", "BirthDate", "CreatedAt", "Price"], tab.DisplayedColumns);
        Assert.Empty(tab.HiddenColumns);
        Assert.Equal(0, tab.FrozenColumnCount);
    }

    [Fact]
    public async Task HideReorderAndFreeze_ChangeTheDisplayedColumns()
    {
        var tab = await OpenTabAsync();

        await tab.HideColumnAsync("IsActive");
        await tab.MoveColumnAsync("Price", 1);
        await tab.FreezeColumnsThroughAsync("Price");

        Assert.Equal(["Id", "Price", "Name", "BirthDate", "CreatedAt"], tab.DisplayedColumns);
        Assert.Equal(["IsActive"], tab.HiddenColumns);
        Assert.Equal(2, tab.FrozenColumnCount);

        await tab.ShowColumnAsync("IsActive");
        await tab.UnfreezeColumnsAsync();

        Assert.Contains("IsActive", tab.DisplayedColumns);
        Assert.Equal(0, tab.FrozenColumnCount);
    }

    [Fact]
    public async Task Layout_IsRestoredWhenTheTableIsReopened()
    {
        var tab = await OpenTabAsync();
        await tab.HideColumnAsync("BirthDate");
        await tab.MoveColumnAsync("Name", 0);
        await tab.FreezeColumnsThroughAsync("Id");

        var reopened = await OpenTabAsync();

        Assert.Equal(["Name", "Id", "IsActive", "CreatedAt", "Price"], reopened.DisplayedColumns);
        Assert.Equal(["BirthDate"], reopened.HiddenColumns);
        Assert.Equal(2, reopened.FrozenColumnCount);
    }

    [Fact]
    public async Task Layout_IsNotSharedWithOtherTables()
    {
        var tab = await OpenTabAsync("Customers");
        await tab.HideColumnAsync("Name");

        var other = await OpenTabAsync("Orders");

        Assert.Contains("Name", other.DisplayedColumns);
    }

    [Fact]
    public async Task HidingTheLastVisibleColumn_IsRefused()
    {
        var tab = await OpenTabAsync();
        foreach (var column in new[] { "Id", "Name", "IsActive", "BirthDate", "CreatedAt" })
            await tab.HideColumnAsync(column);

        await tab.HideColumnAsync("Price");

        Assert.Equal(["Price"], tab.DisplayedColumns);
    }

    [Fact]
    public async Task SavedLayout_ToleratesColumnsAddedOrDroppedSinceItWasSaved()
    {
        var tab = await OpenTabAsync();
        await new QueryWorkspaceService(_workspacePath).SaveTableColumnLayoutAsync(tab.Session!.Connection, tab.TableObjectName,
            new TableColumnLayout { Order = ["Dropped", "Price", "Id"], Hidden = ["Name", "Dropped"], FrozenCount = 9 });

        var reopened = await OpenTabAsync();

        Assert.Equal(["Price", "Id", "IsActive", "BirthDate", "CreatedAt"], reopened.DisplayedColumns);
        Assert.Equal(["Name"], reopened.HiddenColumns);
        Assert.Equal(5, reopened.FrozenColumnCount);
    }

    [Fact]
    public async Task MovingColumns_KeepsTheSameColumnsFrozen()
    {
        var tab = await OpenTabAsync();
        await tab.FreezeColumnsThroughAsync("Name");

        await tab.MoveColumnAsync("Id", 3);
        Assert.Equal(["Name", "IsActive", "BirthDate", "Id", "CreatedAt", "Price"], tab.DisplayedColumns);
        Assert.Equal(1, tab.FrozenColumnCount);

        await tab.MoveColumnAsync("Price", 0);
        Assert.Equal(["Price", "Name"], tab.DisplayedColumns.Take(tab.FrozenColumnCount));
    }

    [Fact]
    public async Task ShowAll_KeepsTheSameColumnsFrozen()
    {
        var tab = await OpenTabAsync();
        await tab.FreezeColumnsThroughAsync("Name");
        await tab.HideColumnAsync("Id");
        Assert.Equal(["Name"], tab.DisplayedColumns.Take(tab.FrozenColumnCount));

        await tab.ShowAllColumnsAsync();

        Assert.Equal(["Id", "Name"], tab.DisplayedColumns.Take(tab.FrozenColumnCount));
    }

    [Fact]
    public async Task Layout_IsSharedByATabOpenedWithoutItsSchemaNode()
    {
        var tab = await OpenTabAsync();
        await tab.HideColumnAsync("Name");

        // As a tab restored from the last session opens: by name only.
        var restored = new TableDataTabViewModel("Customers", CreateLayoutSession(),
            new QueryWorkspaceService(_workspacePath), null);
        await restored.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(["Name"], restored.HiddenColumns);
    }
}
