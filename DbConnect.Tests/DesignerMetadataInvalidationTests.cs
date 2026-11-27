using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class DesignerMetadataInvalidationTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownTableSaveOrDropRefreshesItsMetadataWithoutInvalidatingSiblingDetails(bool drop)
    {
        var database = new SchemaChangeTestDatabase();
        var session = database.OpenConnection();
        var vm = CreateMainWindowViewModel();
        try
        {
            await session.ExecuteNonQueryAsync("CREATE TABLE orders (id INTEGER PRIMARY KEY); CREATE TABLE sibling (untouched INTEGER);");
            var root = Assert.Single(await session.GetSchemaAsync());
            vm.ActiveConnections[root] = session.Connection;
            vm.ActiveSessions[root] = session;
            vm.ActiveSessionOptions.Add(session);
            vm.SchemaNodes.Add(root);
            var table = Assert.Single(root.Children, node => node.RawName == "orders");
            var sibling = Assert.Single(root.Children, node => node.RawName == "sibling");
            await vm.SchemaTree.LoadSchemaNodeChildrenAsync(table);
            await vm.SchemaTree.LoadSchemaNodeChildrenAsync(sibling);
            var siblingScope = new MetadataScope(sibling.Identity, SchemaNodeType.Column);
            var siblingSnapshot = session.Catalog.GetSnapshot(siblingScope);
            await Columns(session);

            if (drop) await vm.DropObjectAsync(new SchemaNode { RealNode = table });
            else
            {
                vm.Tools.OpenTableDesignerTab(new SchemaNode { RealNode = table });
                var designer = Assert.IsType<TableDesignerViewModel>(vm.SelectedTab);
                await designer.LoadAsync();
                designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
                await designer.SaveCommand.ExecuteAsync(null);
                Assert.Contains("successfully", designer.StatusMessage);
            }

            Assert.Equal(siblingSnapshot, session.Catalog.GetSnapshot(siblingScope));
            Assert.Equal(drop ? [] : new[] { "id", "added" }, await Columns(session));
            Assert.Equal(!drop, root.Children.Any(node => node.RawName == "orders"));
            if (!drop) Assert.Contains(table.Children, node => node.RawName == "added");
        }
        finally
        {
            database.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task FailedViewReplacementRefreshesAfterTheEarlierDropSucceeded()
    {
        var database = new SchemaChangeTestDatabase();
        var session = database.OpenConnection();
        var vm = CreateMainWindowViewModel();
        try
        {
            await session.ExecuteNonQueryAsync("CREATE VIEW existing_view AS SELECT 1 AS original;");
            var root = Assert.Single(await session.GetSchemaAsync());
            vm.ActiveConnections[root] = session.Connection;
            vm.ActiveSessions[root] = session;
            vm.ActiveSessionOptions.Add(session);
            vm.SchemaNodes.Add(root);
            var view = Assert.Single(root.Children, node => node.RawName == "existing_view");
            vm.Tools.OpenEditViewTab(view);
            var designer = Assert.IsType<ViewDesignerViewModel>(vm.SelectedTab);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (designer.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.False(designer.IsLoading);
            designer.SelectSql = "SELECT FROM";
            await designer.SaveCommand.ExecuteAsync(null);
            Assert.Contains("failed", designer.StatusMessage);
            Assert.DoesNotContain(root.Children, node => node.RawName == "existing_view");
            Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == "existing_view");
        }
        finally
        {
            database.Dispose();
        }
    }
    private static async Task<string[]> Columns(DatabaseSession session) =>
        (await CatalogSqlCompletion.GetCandidatesAsync("SELECT main.orders.", 19, session)).Select(candidate => candidate.Text).ToArray();
}
