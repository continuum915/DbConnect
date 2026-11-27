using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class CatalogCompletionLifecycleTests
{
    [Fact]
    public async Task FailedColumnsRetryAndShareTheExplorerRequest()
    {
        var provider = CatalogCompletionTests.Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ThrowsAsync(new IOException("offline"));
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        Assert.Empty(await Complete(session));
        var schema = (await session.GetSchemaAsync())[0].Children[0].Identity!;
        var relation = Assert.Single(session.Catalog.GetSnapshot(new(schema, SchemaNodeType.Table)).Records).Identity;
        Assert.Equal(MetadataLoadState.Failed, session.Catalog.GetSnapshot(new(relation, SchemaNodeType.Column)).State);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<ColumnDefinition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var completion = Complete(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var explorer = session.Catalog.LoadAsync(new(relation, SchemaNodeType.Column));
        release.SetResult([new ColumnDefinition { Name = "retried", DataType = "int" }]);
        Assert.Equal("retried", Assert.Single(await completion).Text);
        Assert.Equal("retried", Assert.Single((await explorer).Records).Identity.Name);
        role.Verify(p => p.GetColumnsAsync(It.IsAny<string>(), relation), Times.Exactly(2));
    }

    [Fact]
    public async Task DisposedConnectionCannotPublishPendingCompletion()
    {
        var provider = CatalogCompletionTests.Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<ColumnDefinition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var pending = Complete(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Catalog.Dispose();
        release.SetResult([new ColumnDefinition { Name = "old", DataType = "int" }]);
        Assert.Empty(await pending);
        var replacement = new DatabaseSession(new ConnectionInfo(), CatalogCompletionTests.Provider().Object);
        Assert.Equal("id", Assert.Single(await Complete(replacement)).Text);
    }

    [Fact]
    public async Task QualifiedCompletionDoesNotWaitForUnrelatedColumns()
    {
        var provider = CatalogCompletionTests.Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "unrelated", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        var blocked = new TaskCompletionSource<List<ColumnDefinition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.Is<DatabaseObjectIdentity>(id => id.Name == "unrelated")))
            .Returns(blocked.Task);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        Assert.Equal("id", Assert.Single(await Complete(session).WaitAsync(TimeSpan.FromSeconds(2))).Text);
    }
    [Fact]
    public async Task RelationContextDoesNotWaitForColumnMetadata()
    {
        var provider = CatalogCompletionTests.Provider();
        var blocked = new TaskCompletionSource<List<ColumnDefinition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>())).Returns(blocked.Task);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var result = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(result, c => c.Text == "orders");
    }
    [Fact]
    public async Task SeparateConnectionsAndInvalidatedRevisionsKeepTheirOwnColumns()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync((string connection, DatabaseObjectIdentity _) => [new ColumnDefinition { Name = connection, DataType = "int" }]);
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = "first" }, provider.Object);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = "second" }, provider.Object);
        Assert.Equal("first", Assert.Single(await Complete(first)).Text);
        Assert.Equal("second", Assert.Single(await Complete(second)).Text);
        var schema = (await first.GetSchemaAsync())[0].Children[0].Identity!;
        var relation = Assert.Single(first.Catalog.GetSnapshot(new(schema, SchemaNodeType.Table)).Records).Identity;
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetColumnsAsync("first", It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync([new ColumnDefinition { Name = "changed", DataType = "int" }]);
        first.Catalog.Invalidate(new(relation, SchemaNodeType.Column));
        Assert.Equal("changed", Assert.Single(await Complete(first)).Text);
        Assert.Equal("second", Assert.Single(await Complete(second)).Text);
    }
    [Fact]
    public async Task FailedSchemaCannotMakeABareRelationLookUnique()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "other" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder }]
            }))
        }]);
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.Is<DatabaseObjectIdentity>(id => id.Name == "other"), SchemaNodeType.Table))
            .ThrowsAsync(new IOException("offline"));
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        Assert.Empty(await CatalogSqlCompletion.GetCandidatesAsync("SELECT orders.", 14, session));
        Assert.Equal("id", Assert.Single(await Complete(session)).Text);
        var tables = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session);
        Assert.Equal("sales.orders", Assert.Single(tables, c => c.Kind == CompletionKind.Table).Text);
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.Is<DatabaseObjectIdentity>(id => id.Name == "other"), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        Assert.Empty(await CatalogSqlCompletion.GetCandidatesAsync("SELECT orders.", 14, session));
        Assert.Equal("id", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT other.orders.", 20, session)).Text);
    }
    private static Task<IReadOnlyList<SqlCompletionCandidate>> Complete(DatabaseSession session) =>
        CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", "SELECT sales.orders.".Length, session);
}
