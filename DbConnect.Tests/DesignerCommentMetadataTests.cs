using Avalonia.Headless.XUnit;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class DesignerCommentMetadataTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task SavingColumnCommentsRefreshesCatalogAfterAllDesignerMutationsFinish()
    {
        var provider = CatalogCompletionTests.Provider();
        provider.SetupGet(p => p.ProviderName).Returns("PostgreSQL");
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { SupportsObjectComments = true });
        provider.Setup(p => p.GetSupportedDataTypes()).Returns(["INTEGER"]);
        provider.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => [new ColumnDefinition { Name = "id", DataType = "INTEGER" }]);
        provider.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        var comment = "before";
        provider.Setup(p => p.GetColumnCommentsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<string, string> { ["id"] = comment });
        // The comment is part of the reviewed plan; the fake engine applies it when that SQL runs.
        var executed = new List<string>();
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync((string _, IReadOnlyList<string> steps) =>
            {
                executed.Add(string.Join(Environment.NewLine, steps));
                if (steps.Any(sql => sql.Contains("""COMMENT ON COLUMN""") && sql.Contains("""."id" IS 'after';"""))) comment = "after";
                return TransactionResult.Success(0);
            });
        provider.As<IStructuredSchemaProvider>().Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync(() => [new ColumnDefinition { Name = "id", DataType = "INTEGER", Comment = comment }]);
        provider.As<ITableDesignSource>().Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DatabaseObjectIdentity table, CancellationToken _) =>
                new TableDesign(table, "PostgreSQL") { Columns = [new TableDesignColumn("id", "INTEGER", true) { Comment = comment }] });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var workspace = CreateMainWindowViewModel();
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);
        var folder = root.Children[0].Children[0];
        await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(folder);
        var table = Assert.Single(folder.Children);
        var designer = new TableDesignerViewModel("orders", session, table);
        await designer.LoadAsync();
        designer.Columns[0].Comment = "after";
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
        var preview = designer.PreviewSql;
        Assert.Contains("""COMMENT ON COLUMN""", preview);
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", designer.StatusMessage);
        // Without transactional DDL each step runs on its own; together they are the previewed SQL.
        Assert.Equal(preview, string.Join(Environment.NewLine, executed));
        Assert.Equal("after", Assert.Single(catalog.GetSnapshot(new(table.Identity, SchemaNodeType.Column)).Records).Description);
        Assert.Equal("after", Assert.Single(table.Children, node => node.Type == SchemaNodeType.Column).Description);
    }
}
