using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class PendingSessionLifetimeTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task SessionCleanupErrorAfterTransactionEndsDoesNotStrandPendingCatalogOwnership()
    {
        var provider = CatalogCompletionTests.Provider();
        var dbSession = new Mock<IDbSession>();
        var active = false;
        dbSession.SetupGet(session => session.Provider).Returns(provider.Object);
        dbSession.SetupGet(session => session.IsInTransaction).Returns(() => active);
        dbSession.Setup(session => session.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { active = true; return Task.CompletedTask; });
        dbSession.Setup(session => session.ExecuteQueryMultipleAsync(It.IsAny<string>(), It.IsAny<QueryExecutionOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Results());
        dbSession.Setup(session => session.DisposeAsync())
            .Returns(() => { active = false; return ValueTask.FromException(new IOException("cleanup failed")); });
        provider.Setup(p => p.OpenSessionAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(dbSession.Object);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var workspace = CreateMainWindowViewModel();
        var root = Assert.Single(await session.GetSchemaAsync());
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);
        workspace.ActiveSessionOptions.Add(session);
        var tab = workspace.NewQueryTabWithSession(session);
        tab.IsManualCommit = true;
        tab.QueryText = "ALTER TABLE orders ADD COLUMN added INTEGER;";
        await workspace.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
        await tab.ReleaseDbSessionAsync();
        Assert.False(tab.HasDbSession);
        Assert.Equal(MetadataLoadState.Loaded, root.MetadataState);
    }

    private static async IAsyncEnumerable<QueryResult> Results()
    {
        await Task.CompletedTask;
        yield return new QueryResult();
    }
}
