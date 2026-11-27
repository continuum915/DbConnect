using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class CatalogNameSemanticsCancellationTests
{
    [Theory]
    [InlineData(SqlBatchDialect.MySql)]
    [InlineData(SqlBatchDialect.SqlServer)]
    public async Task DisposingRootLoadCancelsTheDriverDuringNameSemanticsDiscovery(SqlBatchDialect dialect)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { BatchDialect = dialect });
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode { Name = "database", Type = SchemaNodeType.Database }]);
        provider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .Returns(async (string _, string _, QueryExecutionOptions? options) =>
            {
                var token = options?.CancellationToken ?? default;
                entered.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
                return new QueryResult();
            });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var owner = session.Catalog;
        var pending = owner.LoadAsync(MetadataScope.Root);
        var driverToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(driverToken.CanBeCanceled);
        owner.Dispose();
        Assert.True(driverToken.IsCancellationRequested);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(owner.GetSnapshot(MetadataScope.Root).Records);
    }
}
