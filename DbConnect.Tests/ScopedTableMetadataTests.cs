using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ScopedTableMetadataTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenTableRefreshUsesNewOrderColumnsAtTheSameStructuredAddress(bool hasUnsavedEdits)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-table-refresh-{Guid.NewGuid():N}.sqlite");
        var provider = new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        TableDataTabViewModel? tab = null;
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString, "CREATE TABLE orders (obsolete INTEGER); INSERT INTO orders VALUES (1);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var table = Assert.Single(root.Children, node => node.RawName == "orders");
            tab = new TableDataTabViewModel("orders", session, table);
            await tab.RefreshAsync();
            Assert.Equal(["obsolete"], tab.Columns);
            var identity = tab.ObjectSelection.Identity;
            var workspace = CreateMainWindowViewModel();
            workspace.ActiveConnections[root] = session.Connection;
            workspace.ActiveSessions[root] = session;
            workspace.SchemaNodes.Add(root);
            workspace.Tabs.Add(tab);
            if (hasUnsavedEdits)
            {
                tab.AllowAllColumnIdentityFallback = true;
                tab.Rows[0].Values["obsolete"] = 42L;
                tab.MarkRowModified(tab.Rows[0], "obsolete");
                Assert.True(tab.HasDirtyRows);
            }

            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "DROP TABLE orders; CREATE TABLE orders (added INTEGER PRIMARY KEY); INSERT INTO orders VALUES (8);");
            await workspace.SchemaTree.RefreshNodeAsync(root);

            if (hasUnsavedEdits)
            {
                Assert.True(tab.HasDirtyRows);
                Assert.Equal(42L, Assert.Single(tab.Rows).Values["obsolete"]);
                Assert.Contains("unsaved", tab.StatusMessage);
                tab.RevertCommand.Execute(null);
                await tab.RefreshAsync();
            }

            Assert.Equal(identity, tab.ObjectSelection.Identity);
            Assert.Equal(["added"], tab.Columns);
            Assert.Equal(8L, Assert.Single(tab.Rows).Values["added"]);
            Assert.True(tab.HasReliableIdentity);
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
