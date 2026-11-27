using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class SchemaChangeFailureTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task FailedBoundaryAndAnotherTabsPendingDdlCannotReleaseTheSharedSnapshot()
    {
        var provider = CatalogCompletionTests.Provider();
        var firstInTransaction = true;
        var secondInTransaction = true;
        var firstDb = new Mock<IDbSession>();
        var secondDb = new Mock<IDbSession>();
        firstDb.SetupGet(db => db.Provider).Returns(provider.Object);
        secondDb.SetupGet(db => db.Provider).Returns(provider.Object);
        firstDb.SetupGet(db => db.IsInTransaction).Returns(() => firstInTransaction);
        secondDb.SetupGet(db => db.IsInTransaction).Returns(() => secondInTransaction);
        firstDb.Setup(db => db.ExecuteQueryMultipleAsync(It.IsAny<string>(), It.IsAny<QueryExecutionOptions>(), It.IsAny<CancellationToken>())).Returns(() => Results());
        secondDb.Setup(db => db.ExecuteQueryMultipleAsync(It.IsAny<string>(), It.IsAny<QueryExecutionOptions>(), It.IsAny<CancellationToken>())).Returns(() => Results());
        firstDb.Setup(db => db.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Commit failed; transaction still open."));
        firstDb.Setup(db => db.RollbackAsync(It.IsAny<CancellationToken>())).Callback(() => firstInTransaction = false).Returns(Task.CompletedTask);
        secondDb.Setup(db => db.RollbackAsync(It.IsAny<CancellationToken>())).Callback(() => secondInTransaction = false).Returns(Task.CompletedTask);
        provider.SetupSequence(p => p.OpenSessionAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstDb.Object).ReturnsAsync(secondDb.Object);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var vm = CreateMainWindowViewModel();
        vm.ActiveSessionOptions.Add(session);
        var first = vm.NewQueryTabWithSession();
        first.SelectedSession = session;
        var second = vm.NewQueryTabWithSession(session);
        first.IsManualCommit = second.IsManualCommit = true;
        var root = Assert.Single(await session.GetSchemaAsync());
        vm.ActiveSessions[root] = session;
        vm.ActiveConnections[root] = session.Connection;
        vm.SchemaNodes.Add(root);
        try
        {
            await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session);
            vm.SelectedTab = first;
            first.QueryText = "ALTER TABLE sales.orders ADD a INT";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            vm.SelectedTab = second;
            second.QueryText = "ALTER TABLE sales.orders ADD b INT";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            await first.CommitCommand.ExecuteAsync(null);
            Assert.True(first.CurrentResult!.HasError);
            Assert.Equal(MetadataLoadState.Pending, catalog.GetSnapshot(MetadataScope.Root).State);
            await second.RollbackCommand.ExecuteAsync(null);
            await catalog.RefreshAsync(MetadataScope.Root);
            Assert.Equal(MetadataLoadState.Pending, catalog.GetSnapshot(MetadataScope.Root).State);
            Assert.Contains("transaction", catalog.GetSnapshot(MetadataScope.Root).Error);
            await first.RollbackCommand.ExecuteAsync(null);
            Assert.Equal(MetadataLoadState.Loaded, catalog.GetSnapshot(MetadataScope.Root).State);
            Assert.Null(catalog.GetSnapshot(MetadataScope.Root).Error);
        }
        finally
        {
            await first.ReleaseDbSessionAsync();
            await second.ReleaseDbSessionAsync();
        }

        static async IAsyncEnumerable<QueryResult> Results()
        {
            await Task.CompletedTask;
            yield return new QueryResult();
        }
    }
    [AvaloniaTheory]
    [InlineData("failure")]
    [InlineData("cancellation")]
    [InlineData("exception")]
    public async Task UnsuccessfulDdlMayHaveChangedStructureAndRefreshesCommittedMetadata(string outcome)
    {
        var provider = CatalogCompletionTests.Provider();
        var database = new Mock<IDbSession>();
        database.SetupGet(db => db.Provider).Returns(provider.Object);
        database.Setup(db => db.ExecuteQueryMultipleAsync(It.IsAny<string>(), It.IsAny<QueryExecutionOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Execute());
        provider.Setup(p => p.OpenSessionAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(database.Object);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        var root = Assert.Single(await session.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
        tree.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await tree.LoadSchemaNodeChildrenAsync(folder);
        var table = Assert.Single(folder.Children);
        await tree.LoadSchemaNodeChildrenAsync(table);
        Assert.Equal("id", Assert.Single(await Complete()).Text);

        tab.QueryText = "ALTER TABLE sales.orders ADD changed INTEGER;";
        if (outcome == "exception") await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExecuteQueryCommand.ExecuteAsync(null));
        else
        {
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.True(tab.CurrentResult!.HasError);
            Assert.Equal(outcome == "cancellation", tab.CurrentResult.Error?.IsCanceled == true);
        }
        Assert.Equal("changed", Assert.Single(await Complete()).Text);
        Assert.Equal("changed", Assert.Single(table.Children, node => node.Type == SchemaNodeType.Column).RawName);

        Task<IReadOnlyList<SqlCompletionCandidate>> Complete() => CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session);
        async IAsyncEnumerable<QueryResult> Execute()
        {
            // Simulate a driver batch whose earlier DDL took effect before its final failure.
            provider.As<IStructuredSchemaProvider>().Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
                .ReturnsAsync([new ColumnDefinition { Name = "changed", DataType = "integer" }]);
            await Task.Yield();
            if (outcome == "exception") throw new InvalidOperationException("Driver failed after DDL.");
            yield return new QueryResult
            {
                Error = outcome == "cancellation" ? new QueryErrorInfo { Provider = "test", Summary = "Canceled", IsCanceled = true } : QueryErrorInfo.Failure("test", "Batch failed.", null)
            };
        }
    }
}
