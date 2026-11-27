using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class CatalogExplorerTests
{
    [AvaloniaFact]
    public void CompletionInsertsQuotedCatalogTextWhileKeepingItsLabel()
    {
        var area = new AvaloniaEdit.Editing.TextArea { Document = new AvaloniaEdit.Document.TextDocument("sel") };
        var completion = new SqlCompletionData("select", CompletionKind.Table, insertionText: "\"select\"");
        completion.Complete(area, new AvaloniaEdit.Document.SimpleSegment(0, 3), EventArgs.Empty);
        Assert.Equal("select", completion.Text);
        Assert.Equal("\"select\"", area.Document.Text);
    }
    [AvaloniaFact]
    public async Task BrowseSharesCatalogSnapshotsAndFailedRefreshRetainsVisibleChildrenUntilRetry()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT"));
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "database", Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Name = "sales", Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }] }]
        }]);
        var role = provider.As<IStructuredSchemaProvider>();
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { fetching.TrySetResult(); return release.Task; });
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = "effective-settings" }, provider.Object);
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        var onUi = true;
        folder.PropertyChanged += (_, _) => onUi &= Dispatcher.UIThread.CheckAccess();
        var operation = tree.LoadSchemaNodeChildrenAsync(folder);
        await fetching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MetadataLoadState.Loading, folder.MetadataState);
        Assert.Equal("Expand to load", Assert.Single(folder.Children).Name);
        var shared = session.Catalog.LoadAsync(new(folder.Identity!.Parent!, SchemaNodeType.Table));
        var completion = CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM sales.", 20, session);
        release.SetResult([new SchemaNode { Name = "orders", RawName = "orders", Type = SchemaNodeType.Table }]);
        await operation;
        Assert.Equal("orders", Assert.Single(folder.Children).RawName);
        Assert.Equal("orders", Assert.Single((await shared).Records).Identity.Name);
        Assert.Equal("orders", Assert.Single(await completion).Text);
        role.Verify(p => p.GetChildrenAsync("effective-settings", folder.Identity!.Parent!, SchemaNodeType.Table), Times.Once);
        // Editing a UI projection cannot mutate canonical metadata or another projection.
        folder.Children[0].Name = "edited UI";
        Assert.Equal("orders", Assert.Single((await shared).Records).DisplayName);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ThrowsAsync(new IOException("Password=secret"));
        await tree.RefreshNodeAsync(new SchemaNode { RealNode = folder });
        Assert.Equal(MetadataLoadState.Stale, folder.MetadataState);
        Assert.Equal("edited UI", Assert.Single(folder.Children).Name);
        Assert.Contains("Refresh to retry", folder.MetadataStatus);
        Assert.DoesNotContain("secret", folder.MetadataError);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "recovered", Type = SchemaNodeType.Table }]);
        Assert.Equal("orders", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM sales.", 20, session)).Text);
        await tree.RefreshNodeAsync(folder);
        Assert.Equal("recovered", Assert.Single(folder.Children).RawName);
        Assert.Equal("recovered", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM sales.", 20, session)).Text);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([]);
        await tree.RefreshNodeAsync(folder);
        Assert.Equal(MetadataLoadState.LoadedEmpty, folder.MetadataState);
        Assert.True(folder.IsLoaded);
        Assert.Empty(folder.Children);
        Assert.Empty(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM sales.", 20, session));
        Assert.True(onUi);
        tree.RemoveSession(root);
        Assert.True(session.Catalog.IsDisposed);
    }
}
