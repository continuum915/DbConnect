using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ScopedMetadataWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(SchemaNodeType.Schema)]
    [InlineData(SchemaNodeType.TableFolder)]
    [InlineData(SchemaNodeType.Database)]
    public async Task ExplicitRefreshReconcilesDiscoveryColumnsAndOpenTableWithoutCrossingConnections(SchemaNodeType target)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-scoped-{Guid.NewGuid():N}.duckdb");
        var provider = new DuckDbProvider();
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = first.ConnectionString }, provider);
        try
        {
            await first.ExecuteNonQueryAsync("CREATE SCHEMA sales; CREATE SCHEMA other; CREATE TABLE sales.orders (original INTEGER); CREATE TABLE sales.removed (id INTEGER); CREATE TABLE other.orders (untouched INTEGER);");
            var root = Assert.Single(await first.GetSchemaAsync());
            var otherRoot = Assert.Single(await second.GetSchemaAsync());
            var tree = new SchemaTreeViewModel(new() { [root] = first.Connection, [otherRoot] = second.Connection },
                new() { [root] = first, [otherRoot] = second }, new());
            tree.SchemaNodes.Add(root);
            tree.SchemaNodes.Add(otherRoot);
            var schema = Assert.Single(root.Children, n => n.RawName == "sales");
            var unrelated = Assert.Single(root.Children, n => n.RawName == "other");
            var folder = Assert.Single(schema.Children, n => n.Type == SchemaNodeType.TableFolder);
            await tree.LoadSchemaNodeChildrenAsync(folder);
            var table = Assert.Single(folder.Children, n => n.RawName == "orders");
            await tree.LoadSchemaNodeChildrenAsync(table);
            table.IsExpanded = true;
            var selection = new TableObjectSelection(first, table);
            await selection.LoadOrderColumnsAsync(first, [], []);
            Assert.Equal("original", Assert.Single(await Complete(first)).Text);
            Assert.Equal("original", Assert.Single(await Complete(second)).Text);
            tree.SchemaSearchText = "orders";
            await tree.Search.Completion;

            await first.ExecuteNonQueryAsync("ALTER TABLE sales.orders RENAME COLUMN original TO changed; ALTER TABLE sales.removed RENAME TO renamed; CREATE TABLE sales.added (id INTEGER);");
            var node = target == SchemaNodeType.Database ? root : target == SchemaNodeType.Schema ? schema : folder;
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = node });
            await tree.Search.Completion;

            Assert.Equal(["added", "orders", "renamed"], folder.Children.Select(n => n.RawName).Order());
            Assert.Same(table, Assert.Single(folder.Children, n => n.RawName == "orders"));
            Assert.True(table.IsExpanded);
            Assert.Equal("changed", Assert.Single(table.Children, n => n.Type == SchemaNodeType.Column).RawName);
            Assert.Equal("changed", Assert.Single(await Complete(first)).Text);
            Assert.Equal("original", Assert.Single(await Complete(second)).Text);
            Assert.Equal("orders", tree.SchemaSearchText);
            Assert.Same(unrelated, Assert.Single(root.Children, n => n.RawName == "other"));
            var metadata = await selection.LoadOrderColumnsAsync(first, [], ["original"]);
            Assert.Equal(["changed"], metadata.Order);
            Assert.Equal(table.Identity, selection.Identity);
        }
        finally { first.Catalog.Dispose(); second.Catalog.Dispose(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task RefreshUpdatesOpenTabMetadataAndDeletedObjectsRequireReselection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-tab-refresh-{Guid.NewGuid():N}.db");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, new SqliteProvider());
        TableDataTabViewModel? tab = null;
        try
        {
            await session.ExecuteNonQueryAsync("CREATE TABLE orders (id INTEGER PRIMARY KEY); INSERT INTO orders VALUES (1);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var table = Assert.Single(root.Children, n => n.RawName == "orders");
            var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
            tree.SchemaNodes.Add(root);
            tab = new TableDataTabViewModel("orders", session, table);
            await tab.RefreshAsync();
            Assert.True(tab.HasReliableIdentity);
            var identity = tab.ObjectSelection.Identity;
            await session.ExecuteNonQueryAsync("DROP TABLE orders; CREATE TABLE orders (changed INTEGER); INSERT INTO orders VALUES (2);");
            await tree.RefreshNodeAsync(root);
            Assert.False(tab.HasReliableIdentity);
            Assert.Contains("metadata", tab.StatusMessage);
            Assert.Equal(identity, tab.ObjectSelection.Identity);
            await tab.RefreshAsync();
            Assert.Equal(["changed"], tab.Columns);
            await session.ExecuteNonQueryAsync("ALTER TABLE orders RENAME TO renamed;");
            await tree.RefreshNodeAsync(table);
            Assert.DoesNotContain(root.Children, n => n.RawName == "orders");
            Assert.False(await tab.ObjectSelection.ResolveAsync(session));
            Assert.Equal(identity, tab.ObjectSelection.Identity);
            Assert.True(tab.ObjectSelection.RequiresReselection);
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
    [AvaloniaFact]
    public async Task RefreshingADeletedSchemaRemovesItsMembershipWithoutRedirectingItsIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-schema-drop-{Guid.NewGuid():N}.duckdb");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, new DuckDbProvider());
        try
        {
            await session.ExecuteNonQueryAsync("CREATE SCHEMA sales; CREATE TABLE sales.orders (id INTEGER);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var schema = Assert.Single(root.Children, n => n.RawName == "sales");
            var identity = schema.Identity;
            var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
            tree.SchemaNodes.Add(root);
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), c => c.Text == "sales");
            await session.ExecuteNonQueryAsync("DROP SCHEMA sales CASCADE; CREATE SCHEMA replacement; CREATE TABLE replacement.orders (id INTEGER);");
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = schema });
            Assert.DoesNotContain(root.Children, n => n.RawName == "sales");
            Assert.Contains(root.Children, n => n.RawName == "replacement");
            Assert.True(session.Catalog.IsKnownMissing(identity!));
            Assert.Equal(identity, schema.Identity);
            Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), c => c.Text == "sales");
        }
        finally { session.Catalog.Dispose(); File.Delete(path); }
    }
    private static Task<IReadOnlyList<SqlCompletionCandidate>> Complete(DatabaseSession session) =>
        CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.orders.", 20, session);
}
