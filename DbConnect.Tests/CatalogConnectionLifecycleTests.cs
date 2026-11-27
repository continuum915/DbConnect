using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class CatalogConnectionLifecycleTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task DisconnectPreventsLateTreePublicationAndReconnectHasIndependentGeneration()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT"));
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "database", Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Name = "sales", Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }] }]
        }]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var saved = new ConnectionInfo { Name = "saved", ConnectionString = "effective-settings" };
        var session = new DatabaseSession(saved, provider.Object);
        var workspace = CreateMainWindowViewModel();
        var root = Assert.Single(await session.GetSchemaAsync());
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = saved;
        workspace.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        var operation = workspace.LoadSchemaNodeChildrenAsync(folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workspace.Disconnect(root);
        release.SetResult([new SchemaNode { Name = "late", Type = SchemaNodeType.Table }]);
        await operation;
        Assert.Empty(workspace.SchemaNodes);
        Assert.True(session.Catalog.IsDisposed);
        Assert.DoesNotContain(folder.Children, child => child.Name == "late");
        var reconnected = new DatabaseSession(saved, provider.Object);
        using var ownership = reconnected.Catalog;
        Assert.NotEqual(session.Objects.Generation, reconnected.Objects.Generation);
        Assert.Equal(MetadataLoadState.NotLoaded, reconnected.Catalog.GetSnapshot(MetadataScope.Root).State);
        Assert.Throws<InvalidOperationException>(() => reconnected.Objects.Validate(folder.Identity!));
        var nextRoot = Assert.Single(await reconnected.GetSchemaAsync());
        Assert.Equal(reconnected.Objects.Generation, nextRoot.Identity!.Generation);
    }

    [AvaloniaFact]
    public void ExplorerRendersDistinctFailureAndStaleStatesWithRefreshAvailable()
    {
        var workspace = CreateMainWindowViewModel();
        var node = new SchemaNode { Name = "database", Type = SchemaNodeType.Database, MetadataState = MetadataLoadState.Failed };
        workspace.SchemaNodes.Add(node);
        var window = new MainWindow { DataContext = workspace };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Failed · Refresh to retry");
            Assert.NotNull(window.FindControl<MenuItem>("CtxRefreshConnection"));
            node.MetadataState = MetadataLoadState.Stale;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Stale · Refresh to retry");
        }
        finally { window.Close(); }
    }
}
