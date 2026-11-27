using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ScopedRefreshFailureTests
{
    [AvaloniaFact]
    public async Task FailedRefreshKeepsUsableExplorerAndCompletionSnapshotsAndRetryUpdatesBoth()
    {
        var provider = CatalogCompletionTests.Provider();
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var table = Assert.Single(folder.Children);
        await tree.LoadSchemaNodeChildrenAsync(table);
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ThrowsAsync(new IOException("password=secret"));
        await tree.RefreshNodeAsync(folder);
        Assert.Equal(MetadataLoadState.Stale, folder.MetadataState);
        Assert.Same(table, Assert.Single(folder.Children));
        Assert.Contains("Refresh to retry", folder.MetadataStatus);
        Assert.Equal("id", Assert.Single(await Complete(session)).Text);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync([new ColumnDefinition { Name = "retried", DataType = "int" }]);
        await tree.RefreshNodeAsync(folder);
        Assert.Equal(MetadataLoadState.Loaded, folder.MetadataState);
        Assert.Null(folder.MetadataError);
        Assert.Equal("retried", Assert.Single(table.Children).RawName);
        Assert.Equal("retried", Assert.Single(await Complete(session)).Text);
    }

    [AvaloniaFact]
    public async Task OverlappingRefreshesCannotRestoreAnOlderExplorerOrCompletionResult()
    {
        var provider = CatalogCompletionTests.Provider();
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var old = tree.RefreshNodeAsync(folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "newest", Type = SchemaNodeType.Table }]);
        await tree.RefreshNodeAsync(folder);
        release.SetResult([new SchemaNode { Name = "old", Type = SchemaNodeType.Table }]);
        await old;
        Assert.Equal("newest", Assert.Single(folder.Children).RawName);
        var result = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM sales.", 20, session);
        Assert.Equal("newest", Assert.Single(result).Text);
    }

    [AvaloniaFact]
    public async Task AnOlderPageCannotReenableEditingAfterMetadataRefresh()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.As<IStructuredSchemaProvider>().Setup(p => p.GetEnumColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>())).ReturnsAsync(new Dictionary<string, EnumColumnInfo>());
        var database = new Mock<IDbSession>();
        database.SetupGet(db => db.Provider).Returns(provider.Object);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TablePageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(db => db.LoadTablePageAsync(It.IsAny<string>(), It.IsAny<TableQuerySpec>(), It.IsAny<QueryExecutionOptions>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        provider.Setup(p => p.OpenSessionAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(database.Object);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var table = Assert.Single(folder.Children);
        var tab = new TableDataTabViewModel("orders", session, table);
        try
        {
            var page = tab.RefreshAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.As<IStructuredSchemaProvider>()
                .Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
                .ReturnsAsync([new ColumnDefinition { Name = "new_key", DataType = "int", IsPrimaryKey = true }]);
            await tree.RefreshNodeAsync(table);
            Assert.False(tab.CanEditExistingRows);
            release.SetResult(new TablePageResult
            {
                Result = new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "id", Ordinal = 0 }],
                    ObjectRows = [new object?[] { 1 }]
                }
            });
            await page;
            Assert.False(tab.CanEditExistingRows);
            Assert.Null(tab.Editing.Tracker);
            Assert.DoesNotContain("id", tab.Columns);
            Assert.Contains("metadata", tab.StatusMessage);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }
    [AvaloniaFact]
    public async Task AnOlderEnumResponseCannotRepopulateMetadataAfterRefresh()
    {
        var provider = CatalogCompletionTests.Provider();
        var database = new Mock<IDbSession>();
        database.SetupGet(db => db.Provider).Returns(provider.Object);
        database.Setup(db => db.LoadTablePageAsync(It.IsAny<string>(), It.IsAny<TableQuerySpec>(), It.IsAny<QueryExecutionOptions>()))
            .ReturnsAsync(new TablePageResult
            {
                Result = new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "id", Ordinal = 0 }],
                    ObjectRows = [new object?[] { 1 }]
                }
            });
        provider.Setup(p => p.OpenSessionAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(database.Object);
        var role = provider.As<IStructuredSchemaProvider>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Dictionary<string, EnumColumnInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetEnumColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var table = Assert.Single(folder.Children);
        var tab = new TableDataTabViewModel("orders", session, table);
        try
        {
            var page = tab.RefreshAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tree.RefreshNodeAsync(table);
            release.SetResult(new() { ["id"] = new(["obsolete"], false) });
            await page;
            Assert.Empty(tab.GetEnumValues("id"));
            role.Setup(p => p.GetEnumColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
                .ReturnsAsync(new Dictionary<string, EnumColumnInfo> { ["id"] = new(["current"], false) });
            await tab.RefreshAsync();
            Assert.Equal(["current"], tab.GetEnumValues("id"));
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }
    private static Task<IReadOnlyList<SqlCompletionCandidate>> Complete(DatabaseSession session) =>
        CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session);
}
