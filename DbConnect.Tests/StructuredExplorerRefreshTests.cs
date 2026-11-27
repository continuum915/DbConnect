using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class StructuredExplorerRefreshTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task FolderRefreshUpdatesLoadedColumnsAndCompletionWithoutReplacingSelectedTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-scoped-refresh-{Guid.NewGuid():N}.duckdb");
        var provider = new DuckDbProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "CREATE SCHEMA sales; CREATE TABLE sales.orders (id INTEGER PRIMARY KEY, obsolete INTEGER);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
            tree.SchemaNodes.Add(root);
            var schema = Assert.Single(root.Children, node => node.RawName == "sales");
            var folder = Assert.Single(schema.Children, node => node.Type == SchemaNodeType.TableFolder);
            await tree.LoadSchemaNodeChildrenAsync(folder);
            var table = Assert.Single(folder.Children);
            table.IsExpanded = true;
            await tree.LoadSchemaNodeChildrenAsync(table);
            Assert.Contains(await Complete(), candidate => candidate.Text == "obsolete");
            tree.SchemaSearchText = "orders";
            await tree.Search.Completion;

            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "ALTER TABLE sales.orders DROP COLUMN obsolete; ALTER TABLE sales.orders ADD COLUMN added INTEGER;");
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = folder });
            await tree.Search.Completion;

            Assert.Same(table, Assert.Single(folder.Children));
            Assert.True(table.IsExpanded);
            Assert.Equal(["id", "added"], table.Children.Where(node => node.Type == SchemaNodeType.Column).Select(node => node.RawName));
            Assert.Equal(["added", "id"], (await Complete()).Select(candidate => candidate.Text).Order());
            Assert.Equal("orders", tree.SchemaSearchText);

            Task<IReadOnlyList<SqlCompletionCandidate>> Complete() =>
                CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session);
        }
        finally { session.Catalog.Dispose(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task FilteredSchemaFolderAndTableRefreshRetainTheirSelectedAddress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-object-refresh-{Guid.NewGuid():N}.duckdb");
        var provider = new DuckDbProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "CREATE SCHEMA \"sales.eu\"; CREATE SCHEMA archive; CREATE TABLE \"sales.eu\".\"order.items\" (selected INTEGER); CREATE TABLE archive.\"order.items\" (other INTEGER);");
            var workspace = CreateMainWindowViewModel();
            var root = Assert.Single(await session.GetSchemaAsync());
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            var schema = Assert.Single(root.Children, node => node.RawName == "sales.eu");
            var folder = Assert.Single(schema.Children, node => node.Type == SchemaNodeType.TableFolder);
            await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(new SchemaNode { RealNode = folder });
            var table = Assert.Single(folder.Children);
            var identity = table.Identity;
            workspace.SchemaSearchText = "order.items";
            var displayedRoot = Assert.Single(workspace.FilteredSchemaNodes);
            Assert.Contains(displayedRoot.Children, node => node.RealNode == schema);
            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "ALTER TABLE \"sales.eu\".\"order.items\" ADD COLUMN added INTEGER;");
            await workspace.SchemaTree.RefreshNodeAsync(new SchemaNode { RealNode = table });
            await workspace.SchemaTree.Search.Completion;
            Assert.Equal(identity, table.Identity);
            Assert.Equal(["selected", "added"], table.Children.Where(node => node.Type == SchemaNodeType.Column).Select(node => node.RawName));
            Assert.All(table.Children, node => Assert.Equal(identity, node.Identity!.Parent));
        }
        finally { session.Catalog.Dispose(); File.Delete(path); }
    }
}
