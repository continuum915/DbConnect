using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class ScopedSchemaActionTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task KnownDropRefreshesItsFolderWithoutInvalidatingAnotherSchema()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.SetupGet(p => p.ProviderName).Returns("PostgreSQL");
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "archive" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
            }))
        }]);
        var removed = false;
        provider.Setup(p => p.ExecuteNonQueryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => { removed = true; return 0; });
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync((string _, DatabaseObjectIdentity parent, SchemaNodeType _) =>
                parent.Name == "sales" && removed ? [] : [new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var workspace = CreateMainWindowViewModel();
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);
        foreach (var schema in root.Children) await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(schema.Children[0]);
        var otherScope = new MetadataScope(root.Children[1].Identity, SchemaNodeType.Table);
        var other = catalog.GetSnapshot(otherScope);
        var discovery = catalog.GetSnapshot(MetadataScope.Root);
        await workspace.DropObjectAsync(root.Children[0].Children[0].Children[0]);
        Assert.Empty(root.Children[0].Children[0].Children);
        Assert.Same(other, catalog.GetSnapshot(otherScope));
        Assert.Same(discovery, catalog.GetSnapshot(MetadataScope.Root));
        Assert.Equal("orders", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT archive.", 15, session)).Text);
    }
}
