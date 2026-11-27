using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class StructuredObjectNavigationTests : MainWindowTestBase
{
    [Fact]
    public void CopyAsInsertUsesTheSelectedStructuredAddress()
    {
        var session = new DatabaseSession(new ConnectionInfo(), new PostgreSqlProvider());
        var node = new SchemaNode { RawName = "order.items", SchemaPath = "sales.eu", Type = SchemaNodeType.Table };
        var tab = new TableDataTabViewModel("order.items", session, node);
        tab.Columns.Add("id");
        var sql = tab.BuildInsertSql(new EditableRow(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.StartsWith("INSERT INTO \"sales.eu\".\"order.items\"", sql);
    }

    [Fact]
    public void AmbiguousLegacyConnectionNameDoesNotChooseTheFirstConnection()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tabs.Clear();
        foreach (var unused in Enumerable.Range(0, 2))
            workspace.ActiveSessionOptions.Add(new DatabaseSession(new ConnectionInfo { Name = "duplicate" }, new SqliteProvider()));
        WorkspaceSessionRestorer.Restore(workspace, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [new SavedTab { TabType = "TableData", ConnectionName = "duplicate", TableName = "orders" }]
        });
        Assert.Empty(workspace.Tabs);
        Assert.Contains("ambiguous", workspace.StatusMessage);
    }

    [Fact]
    public async Task ReconnectRejectsOldNodeIdentityUntilTheObjectIsSelectedAgain()
    {
        var connection = new ConnectionInfo();
        var original = new DatabaseSession(connection, new SqliteProvider());
        var replacement = new DatabaseSession(connection, new SqliteProvider());
        var node = new SchemaNode { Name = "orders", Type = SchemaNodeType.Table };
        var identity = original.Objects.Bind(node, null);
        var selection = new TableObjectSelection(replacement, node);

        Assert.False(await selection.ResolveAsync(replacement));
        Assert.True(selection.RequiresReselection);
        Assert.False(replacement.Objects.Same(identity, replacement.Objects.Rebind(identity)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.Objects.GetColumnsAsync(identity));
    }

    [Fact]
    public async Task SavedIdentityRoundTripsRawSegmentsAndParentThroughSessionStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-object-session-{Guid.NewGuid():N}");
        try
        {
            var storage = new SessionPersistenceService(directory);
            var parent = new DatabaseObjectIdentity("db.name", "sales.eu", "sales.eu", SchemaNodeType.Schema);
            var identity = new DatabaseObjectIdentity("db.name", "sales.eu", "order.\" ]", SchemaNodeType.Table) { Parent = parent };
            await storage.SaveAsync(new SavedSession { Tabs = [new SavedTab { TabType = "TableData", Object = identity }] });
            var saved = await storage.LoadAsync();
            Assert.Equal(identity, Assert.Single(saved!.Tabs).Object);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void StructuredSavedTabRestoresItsSchemaAndRebindsItsGeneration()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tabs.Clear();
        var connection = new ConnectionInfo();
        var session = new DatabaseSession(connection, new PostgreSqlProvider());
        workspace.ActiveSessionOptions.Add(session);
        var identity = new DatabaseObjectIdentity(null, "sales.eu", "order.items", SchemaNodeType.Table)
        {
            ConnectionId = connection.Id,
            Generation = Guid.NewGuid()
        };

        WorkspaceSessionRestorer.Restore(workspace, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [new SavedTab { TabType = "TableData", ConnectionId = connection.Id, TableName = "order.items", Object = identity }]
        });

        var tab = Assert.IsType<TableDataTabViewModel>(Assert.Single(workspace.Tabs));
        Assert.Equal("sales.eu", tab.ObjectSelection.Identity!.Schema);
        Assert.Equal(session.Objects.Generation, tab.ObjectSelection.Identity.Generation);
        Assert.Equal(connection.Id, tab.ObjectSelection.Identity.ConnectionId);
    }

    [Fact]
    public void DuplicateTableNamesInDifferentSchemasOpenSeparateTabsAndReuseTheSelectedObject()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tabs.Clear();
        var root = new SchemaNode { Name = "Local", Type = SchemaNodeType.Database };
        var first = new SchemaNode { Name = "order.items", RawName = "order.items", SchemaPath = "sales.eu", Type = SchemaNodeType.Table };
        var second = new SchemaNode { Name = "order.items", RawName = "order.items", SchemaPath = "archive", Type = SchemaNodeType.Table };
        root.Children.Add(first);
        root.Children.Add(second);
        var session = new DatabaseSession(new ConnectionInfo(), new PostgreSqlProvider());
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);

        workspace.OpenTableDataTab(first);
        workspace.OpenTableDataTab(second);
        workspace.OpenTableDataTab(new SchemaNode { RealNode = first });

        Assert.Equal(2, workspace.Tabs.Count);
        var selected = Assert.IsType<TableDataTabViewModel>(workspace.SelectedTab);
        Assert.Equal("sales.eu", selected.ObjectSelection.Identity!.Schema);
        Assert.Same(workspace.Tabs[0], selected);
        Assert.Equal("order.items", selected.ObjectSelection.Identity.Name);
    }
}
