using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class ScopedObjectRefreshConformanceTests
{
    [AvaloniaTheory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task ExplicitRefreshTracksExternalChangesAndPreservesIndependentConnectionSnapshots(string providerName)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(connection)) return;
        var provider = new DatabaseService().GetProvider(providerName);
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, provider);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, provider);
        var name = "rain_refresh_" + Guid.NewGuid().ToString("N")[..12];
        var schema = providerName == "PostgreSQL" ? "public" : providerName == "SQL Server" ? "dbo" : null;
        var address = new DatabaseObjectIdentity(null, schema, name, SchemaNodeType.Table);
        var quoted = ObjectNames.Quote(provider.Capabilities, address);
        TableDataTabViewModel? tab = null;
        var exists = false;
        try
        {
            await first.ExecuteNonQueryAsync($"CREATE TABLE {quoted} (id INTEGER PRIMARY KEY, obsolete INTEGER);");
            exists = true;
            var root = Assert.Single(await first.GetSchemaAsync());
            await second.GetSchemaAsync();
            var tree = new SchemaTreeViewModel(new() { [root] = first.Connection }, new() { [root] = first }, new());
            tree.SchemaNodes.Add(root);
            var owner = schema == null ? root : Assert.Single(root.Children, node => node.RawName == schema);
            var folder = schema == null ? null : Assert.Single(owner.Children, node => node.Type == SchemaNodeType.TableFolder);
            if (folder != null) await tree.LoadSchemaNodeChildrenAsync(folder);
            var table = Assert.Single((folder ?? root).Children, node => node.RawName == name);
            await tree.LoadSchemaNodeChildrenAsync(table);
            var identity = table.Identity;
            tab = new TableDataTabViewModel(name, first, table);
            await tab.RefreshAsync();
            var sql = "SELECT " + quoted + ".";
            async Task<string[]> Complete(DatabaseSession session) => (await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, session))
                .Select(candidate => candidate.Text).Order().ToArray();
            Assert.Equal(["id", "obsolete"], await Complete(first));
            Assert.Equal(["id", "obsolete"], await Complete(second));
            await provider.ExecuteNonQueryAsync(second.ConnectionString, $"ALTER TABLE {quoted} DROP COLUMN obsolete; ALTER TABLE {quoted} ADD added INTEGER;");
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = table });
            Assert.Equal(["added", "id"], table.Children.Where(node => node.Type == SchemaNodeType.Column).Select(node => node.RawName).Order());
            Assert.Equal(["added", "id"], await Complete(first));
            Assert.Equal(["id", "obsolete"], await Complete(second));
            Assert.Equal(identity, tab.ObjectSelection.Identity);
            await tab.RefreshAsync();
            Assert.Equal(["id", "added"], tab.Columns);
            await provider.ExecuteNonQueryAsync(second.ConnectionString, $"DROP TABLE {quoted};");
            exists = false;
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = table });
            Assert.DoesNotContain((folder ?? root).Children, node => node.RawName == name);
            Assert.Empty(await Complete(first));
            Assert.False(await tab.ObjectSelection.ResolveAsync(first));
            Assert.Equal(identity, tab.ObjectSelection.Identity);
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            first.Catalog.Dispose();
            second.Catalog.Dispose();
            if (exists) await first.ExecuteNonQueryAsync($"DROP TABLE {quoted};");
        }
    }
}
