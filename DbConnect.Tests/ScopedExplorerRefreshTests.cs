using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ScopedExplorerRefreshTests
{
    [AvaloniaFact]
    public async Task FirstFailedSchemaRefreshRetainsCompletionAndRequiresExplicitRetry()
    {
        var provider = CatalogCompletionTests.Provider();
        var discovery = new List<SchemaNode> { new()
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "archive" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
            }))
        } };
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync(discovery);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var sales = root.Children[0];
        await tree.LoadSchemaNodeChildrenAsync(sales.Children[0]);
        Assert.Equal("id", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session)).Text);
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ThrowsAsync(new IOException("offline"));
        await tree.RefreshNodeAsync(sales);
        Assert.Equal(MetadataLoadState.Failed, sales.MetadataState);
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync(discovery);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.Is<DatabaseObjectIdentity>(id => id.Name == "sales"), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "recovered", Type = SchemaNodeType.Table }]);

        Assert.Equal("orders", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.", 13, session)).Text);
        Assert.Empty(await CatalogSqlCompletion.GetCandidatesAsync("SELECT orders.", 14, session));
        Assert.Equal("orders", Assert.Single(sales.Children[0].Children).RawName);
        await tree.RefreshNodeAsync(sales);
        Assert.Equal("recovered", Assert.Single(sales.Children[0].Children).RawName);
        Assert.Equal("recovered", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.", 13, session)).Text);
    }

    [AvaloniaFact]
    public async Task RefreshingASearchOnlyTableDoesNotRestoreEmbeddedObsoleteColumnsWhenBrowsing()
    {
        var provider = CatalogCompletionTests.Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table,
                Children = [new SchemaNode { Name = "obsolete", Type = SchemaNodeType.Column }] }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        tree.SchemaSearchText = "orders";
        await tree.Search.Completion;
        var displayed = tree.FilteredSchemaNodes[0].Children[0].Children[0].Children[0];
        await tree.RefreshNodeAsync(displayed);
        await tree.Search.Completion;
        Assert.Equal("id", Assert.Single(tree.FilteredSchemaNodes[0].Children[0].Children[0].Children[0].Children).RawName);
        tree.SchemaSearchText = "";
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        Assert.Equal("id", Assert.Single(Assert.Single(folder.Children).Children).RawName);
    }

    [AvaloniaFact]
    public async Task OverlappingRefreshesCannotRestoreAnOlderExplorerOrCompletionSnapshot()
    {
        var provider = CatalogCompletionTests.Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var old = tree.RefreshNodeAsync(folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "current", Type = SchemaNodeType.Table }]);
        await tree.RefreshNodeAsync(folder);
        release.SetResult([new SchemaNode { Name = "obsolete", Type = SchemaNodeType.Table }]);
        await old;
        Assert.Equal("current", Assert.Single(folder.Children).RawName);
        Assert.Equal("current", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.", 13, session)).Text);
    }

    [AvaloniaFact]
    public async Task FilteredSchemaRefreshRemovesRenamedObjectsWithoutInvalidatingAnotherSchema()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "archive" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
            }))
        }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var sales = root.Children[0];
        var archive = root.Children[1];
        await tree.LoadSchemaNodeChildrenAsync(sales.Children[0]);
        await tree.LoadSchemaNodeChildrenAsync(archive.Children[0]);
        await CatalogSqlCompletion.GetCandidatesAsync("SELECT ", 7, session);
        var archiveScope = new MetadataScope(archive.Identity, SchemaNodeType.Table);
        var archiveSnapshot = catalog.GetSnapshot(archiveScope);
        tree.SchemaSearchText = "orders";
        await tree.Search.Completion;
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.Is<DatabaseObjectIdentity>(id => id.Name == "sales"), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "renamed", Type = SchemaNodeType.Table }]);

        await tree.RefreshNodeAsync(new SchemaNode { RealNode = sales });
        await tree.Search.Completion;

        Assert.Same(archiveSnapshot, catalog.GetSnapshot(archiveScope));
        Assert.Equal("renamed", Assert.Single(sales.Children[0].Children).RawName);
        Assert.Equal("orders", tree.SchemaSearchText);
        var candidates = await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.", 13, session);
        Assert.Equal("renamed", Assert.Single(candidates).Text);
    }
}
