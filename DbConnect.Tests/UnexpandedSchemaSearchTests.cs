using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class UnexpandedSchemaSearchTests
{
    [AvaloniaFact]
    public async Task SearchUsesFreshSharedMetadataWithoutReplacingBrowsedChildren()
    {
        var (tree, session, provider, folder) = await CreateTree();
        using var catalog = session.Catalog;
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "old_orders", Type = SchemaNodeType.Table }]);
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var original = Assert.Single(folder.Children);
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ThrowsAsync(new IOException("network"));
        await tree.RefreshNodeAsync(folder);
        tree.SchemaSearchText = "new_orders";
        await tree.Search.Completion;
        Assert.True(tree.Search.IsIncomplete);
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "new_orders", Type = SchemaNodeType.Table }]);
        await tree.Search.RetryCommand.ExecuteAsync(null);
        Assert.False(tree.Search.IsIncomplete);
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);
        Assert.Equal("new_orders", tree.FilteredSchemaNodes[0].Children[0].Children[0].Children[0].Name);
        tree.SchemaSearchText = "old_orders";
        await tree.Search.Completion;
        Assert.Equal("No matches", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "";
        Assert.Same(original, Assert.Single(folder.Children));
        Assert.True(folder.IsLoaded);
    }

    [AvaloniaFact]
    public async Task SearchSharesBrowseLoadAcrossSchemasAndPreservesOriginalRelations()
    {
        var (tree, session, provider, folder) = await CreateTree();
        using var catalog = session.Catalog;
        var archive = new SchemaNode
        {
            Name = "archive",
            Type = SchemaNodeType.Schema,
            Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
        };
        tree.SchemaNodes[0].Children.Add(archive);
        session.Objects.Bind(tree.SchemaNodes[0], null);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns((string _, DatabaseObjectIdentity parent, SchemaNodeType _) => parent.Schema == "archive"
                ? Task.FromResult(new List<SchemaNode> { new() { Name = "orders", Type = SchemaNodeType.Table } })
                : WaitForSales());
        Task<List<SchemaNode>> WaitForSales() { entered.TrySetResult(); return release.Task; }
        tree.SchemaSearchText = "orders";
        var search = tree.Search.Completion;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var browse = tree.LoadSchemaNodeChildrenAsync(folder);
        release.SetResult([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        await Task.WhenAll(search, browse);
        await tree.Search.Completion;
        Assert.Equal("2 matches", tree.SchemaFilterMatchCount);
        var schemas = Assert.Single(tree.FilteredSchemaNodes).Children;
        Assert.Equal(2, schemas.Count);
        Assert.Same(Assert.Single(folder.Children), schemas[0].Children[0].Children[0]);
        Assert.NotEqual(schemas[0].Children[0].Children[0].Identity, schemas[1].Children[0].Children[0].Identity);
        structured.Verify(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table), Times.Exactly(2));
    }

    [AvaloniaFact]
    public async Task FailedReloadKeepsStaleMatchesAndRetryRefreshesThem()
    {
        var (tree, session, provider, folder) = await CreateTree();
        using var catalog = session.Catalog;
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        tree.SchemaSearchText = "orders";
        await tree.Search.Completion;
        catalog.Invalidate(new(folder.Identity!.Parent!, SchemaNodeType.Table));
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ThrowsAsync(new IOException("network"));
        tree.SchemaSearchText = "order";
        await tree.Search.Completion;
        Assert.True(tree.Search.IsIncomplete);
        var stale = tree.FilteredSchemaNodes[0].Children[0].Children[0].Children[0];
        Assert.Equal("orders", stale.Name);
        Assert.Equal(MetadataLoadState.Stale, stale.MetadataState);
        Assert.NotNull(stale.MetadataError);
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table)).ReturnsAsync([]);
        await tree.Search.RetryCommand.ExecuteAsync(null);
        Assert.Equal("No matches", tree.SchemaFilterMatchCount);
    }

    [AvaloniaTheory]
    [InlineData("permission denied")]
    [InlineData("network unavailable")]
    public async Task FailedCoverageIsNotNoMatchesAndRetryRetainsSuccessfulScopes(string error)
    {
        var (tree, session, provider, folder) = await CreateTree();
        using var catalog = session.Catalog;
        var views = new SchemaNode { Name = "Views", Type = SchemaNodeType.ViewFolder, IsLazyLoadable = true };
        tree.SchemaNodes[0].Children[0].Children.Add(views);
        session.Objects.Bind(tree.SchemaNodes[0], null);
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.View))
            .ThrowsAsync(new IOException(error));
        tree.SchemaSearchText = "orders";
        await tree.Search.Completion;
        Assert.True(tree.Search.IsIncomplete);
        Assert.Contains("1 match", tree.SchemaFilterMatchCount);
        Assert.Contains("Incomplete", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "missing";
        await tree.Search.Completion;
        Assert.DoesNotContain("No matches", tree.SchemaFilterMatchCount);
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.View)).ReturnsAsync([]);
        await tree.Search.RetryCommand.ExecuteAsync(null);
        Assert.False(tree.Search.IsIncomplete);
        Assert.Equal("No matches", tree.SchemaFilterMatchCount);
        structured.Verify(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table), Times.Once);
    }

    [AvaloniaFact]
    public async Task OverlappingSearchAndClearKeepSharedRequestsAndIgnoreLateResults()
    {
        var (tree, session, provider, folder) = await CreateTree();
        using var catalog = session.Catalog;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        tree.SchemaNodes.Add(new SchemaNode
        {
            Name = "disconnected",
            Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
        });
        tree.SchemaSearchText = "old";
        var old = tree.Search.Completion;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tree.Search.IsLoading);
        Assert.Contains("Searching", tree.SchemaFilterMatchCount);
        Assert.DoesNotContain("No matches", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "new";
        var current = tree.Search.Completion;
        var shared = catalog.LoadAsync(new(folder.Identity!.Parent!, SchemaNodeType.Table));
        release.SetResult([new SchemaNode { Name = "new_orders", Type = SchemaNodeType.Table }]);
        await Task.WhenAll(old, current, shared);
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);
        Assert.Equal("new_orders", tree.FilteredSchemaNodes[0].Children[0].Children[0].Children[0].Name);
        structured.Verify(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table), Times.Once);

        catalog.Invalidate(new(folder.Identity.Parent!, SchemaNodeType.Table));
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.SchemaSearchText = "new_orders";
        var pending = tree.Search.Completion;
        Assert.Contains("1 match", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "";
        release.SetResult([new SchemaNode { Name = "late", Type = SchemaNodeType.Table }]);
        await pending;
        Assert.Equal("", tree.SchemaFilterMatchCount);
        Assert.False(tree.Search.IsLoading);
        Assert.False(folder.IsLoaded);
        Assert.Equal("Expand to load", Assert.Single(folder.Children).Name);
        await tree.LoadSchemaNodeChildrenAsync(folder);
        Assert.Equal("late", Assert.Single(folder.Children).Name);
    }


    [AvaloniaTheory]
    [InlineData(SchemaNodeType.TableFolder, SchemaNodeType.Table)]
    [InlineData(SchemaNodeType.ViewFolder, SchemaNodeType.View)]
    [InlineData(SchemaNodeType.MaterializedViewFolder, SchemaNodeType.MaterializedView)]
    public async Task SearchDiscoversUnexpandedRelationsWithoutChangingBrowseState(SchemaNodeType folderKind, SchemaNodeType relationKind)
    {
        var (tree, session, provider, folder) = await CreateTree();
        folder.Type = folderKind;
        session.Objects.Bind(tree.SchemaNodes[0], null);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), relationKind))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = relationKind }]);
        tree.SchemaSearchText = "orders";
        await tree.Search.Completion;
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);
        var result = Assert.Single(Assert.Single(Assert.Single(tree.FilteredSchemaNodes).Children).Children);
        var relation = Assert.Single(result.Children);
        Assert.Same(session, tree.FindSessionForNode(relation));
        Assert.Equal("sales", tree.GetIdentity(relation, session).Schema);
        Assert.False(folder.IsLoaded);
        Assert.False(folder.IsExpanded);
        Assert.Equal(MetadataLoadState.NotLoaded, folder.MetadataState);
        tree.SchemaSearchText = "";
        Assert.Same(folder, tree.FilteredSchemaNodes[0].Children[0].Children[0]);
        Assert.Equal("Expand to load", Assert.Single(folder.Children).Name);
        provider.Verify(p => p.GetSchemaAsync(It.IsAny<string>()), Times.Once);
        provider.As<IStructuredSchemaProvider>().Verify(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()), Times.Never);
        session.Catalog.Dispose();
    }

    internal static async Task<(SchemaTreeViewModel Tree, DatabaseSession Session, Mock<IDatabaseProvider> Provider, SchemaNode Folder)> CreateTree()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT"));
        provider.As<IStructuredSchemaProvider>();
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "database", Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Name = "sales", Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }] }]
        }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        return (tree, session, provider, root.Children[0].Children[0]);
    }
}
