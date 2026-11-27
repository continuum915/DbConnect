using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class SchemaChangeInvalidationTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task QueryDdlUpdatesCatalogExplorerAndCompletionOnItsOwnConnection()
    {
        var database = new SchemaChangeTestDatabase();
        var first = database.OpenConnection();
        var second = database.OpenConnection();
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(first);
        tab.SelectedSession = first;
        try
        {
            await first.ExecuteNonQueryAsync("CREATE TABLE orders (original INTEGER);");
            var root = Assert.Single(await first.GetSchemaAsync());
            var tree = new SchemaTreeViewModel(new() { [root] = first.Connection }, new() { [root] = first }, new());
            tree.SchemaNodes.Add(root);
            var table = Assert.Single(root.Children, node => node.RawName == "orders");
            await tree.LoadSchemaNodeChildrenAsync(table);
            Assert.Equal(["original"], await Columns(first));
            Assert.Equal(["original"], await Columns(second));

            tab.QueryText = "ALTER TABLE orders RENAME COLUMN original TO changed; CREATE TABLE added (id INTEGER);";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);

            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            Assert.Equal(["changed"], await Columns(first));
            Assert.Equal(["original"], await Columns(second));
            Assert.Same(table, Assert.Single(root.Children, node => node.RawName == "orders"));
            Assert.Equal("changed", Assert.Single(table.Children, node => node.Type == SchemaNodeType.Column).RawName);
            Assert.Contains(root.Children, node => node.RawName == "added");
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            database.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("typed commit")]
    [InlineData("typed rollback")]
    [InlineData("close")]
    public async Task ManualTransactionKeepsCommittedMetadataPendingUntilItsBoundary(string boundary)
    {
        var database = new SchemaChangeTestDatabase();
        var session = database.OpenConnection();
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        try
        {
            await session.ExecuteNonQueryAsync("CREATE TABLE orders (original INTEGER);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var tree = new SchemaTreeViewModel(new() { [root] = session.Connection }, new() { [root] = session }, new());
            tree.SchemaNodes.Add(root);
            var table = Assert.Single(root.Children, node => node.RawName == "orders");
            await tree.LoadSchemaNodeChildrenAsync(table);
            Assert.Equal(["original"], await Columns(session));
            tab.IsManualCommit = true;
            tab.QueryText = "ALTER TABLE orders RENAME COLUMN original TO changed; CREATE TABLE pending_table (id INTEGER);";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            Assert.True(tab.IsInTransaction);
            Assert.Equal(MetadataLoadState.Pending, session.Catalog.GetSnapshot(MetadataScope.Root).State);
            Assert.Contains("transaction", root.MetadataStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["original"], await Columns(session));
            await tree.RefreshNodeAsync(root);
            Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
            Assert.DoesNotContain(root.Children, node => node.RawName == "pending_table");
            Assert.Equal("original", Assert.Single(table.Children, node => node.Type == SchemaNodeType.Column).RawName);

            switch (boundary)
            {
                case "commit": await tab.CommitCommand.ExecuteAsync(null); break;
                case "rollback": await tab.RollbackCommand.ExecuteAsync(null); break;
                case "close": await tab.ReleaseDbSessionAsync(); break;
                default:
                    tab.QueryText = boundary == "typed commit" ? "COMMIT;" : "ROLLBACK;";
                    await vm.ExecuteQueryCommand.ExecuteAsync(null);
                    break;
            }

            var committed = boundary is "commit" or "typed commit";
            Assert.False(tab.IsInTransaction);
            Assert.Equal(committed ? ["changed"] : ["original"], await Columns(session));
            Assert.Equal(MetadataLoadState.Loaded, root.MetadataState);
            Assert.Equal(committed, root.Children.Any(node => node.RawName == "pending_table"));
            Assert.Equal(committed ? "changed" : "original", Assert.Single(table.Children, node => node.Type == SchemaNodeType.Column).RawName);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            database.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("SELECT 1 AS comment;")]
    [InlineData("SELECT 'CREATE TABLE fake (id INT)' AS text; -- DROP TABLE orders")]
    [InlineData("/* ALTER TABLE orders ADD fake INT */ SELECT * FROM orders;")]
    public async Task OrdinarySelectDoesNotReloadMetadata(string sql)
    {
        var database = new SchemaChangeTestDatabase();
        var session = database.OpenConnection();
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        try
        {
            await session.ExecuteNonQueryAsync("CREATE TABLE orders (original INTEGER);");
            await Columns(session);
            var snapshot = session.Catalog.GetSnapshot(MetadataScope.Root);
            tab.QueryText = sql;
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            Assert.Equal(snapshot, session.Catalog.GetSnapshot(MetadataScope.Root));
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            database.Dispose();
        }
    }
    private static async Task<string[]> Columns(DatabaseSession session) =>
        (await CatalogSqlCompletion.GetCandidatesAsync("SELECT main.orders.", 19, session)).Select(candidate => candidate.Text).ToArray();
}
