using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class ScopedRefreshConformanceTests
{
    [AvaloniaTheory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public async Task ExternalChangesRefreshExplorerCompletionAndOpenTableWithoutChangingAnotherConnection(string providerName)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (providerName is not ("SQLite" or "DuckDB") && string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = providerName is "SQLite" or "DuckDB"
            ? await ProviderConformanceFixture.CreateLocalAsync(providerName)
            : await ProviderConformanceFixture.CreateExternalAsync(providerName, connection!);
        var provider = fixture.Provider;
        await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP VIEW {fixture.QualifiedViewName};");
        if (providerName == "DuckDB")
            await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP INDEX {fixture.Quote(fixture.IndexName)};");
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, provider);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, provider);
        using var firstCatalog = first.Catalog;
        using var secondCatalog = second.Catalog;
        var root = Assert.Single(await first.GetSchemaAsync());
        var tree = new SchemaTreeViewModel(new() { [root] = first.Connection }, new() { [root] = first }, new());
        tree.SchemaNodes.Add(root);
        var address = await first.Objects.ResolveLegacyRelationAsync(fixture.TableName);
        Assert.NotNull(address);
        var owner = root.Children.FirstOrDefault(node => node.Type == SchemaNodeType.Schema && node.RawName == address.Schema);
        if (owner != null)
            await tree.LoadSchemaNodeChildrenAsync(Assert.Single(owner.Children, node => node.Type == SchemaNodeType.TableFolder));
        var table = Find(root, fixture.TableName)!;
        Assert.NotNull(table);
        await tree.LoadSchemaNodeChildrenAsync(table);
        var quoted = ObjectNames.Quote(provider.Capabilities, table.Identity!);
        var sql = "SELECT " + quoted + ".";
        var original = await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, second);
        var tab = new TableDataTabViewModel(fixture.TableName, first, table);
        try
        {
            await tab.RefreshAsync();
            var identity = tab.ObjectSelection.Identity;
            await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"ALTER TABLE {quoted} ADD refreshed_column INTEGER;");
            await tree.RefreshNodeAsync(new SchemaNode { RealNode = table });
            await tab.RefreshAsync();

            Assert.Equal(identity, tab.ObjectSelection.Identity);
            Assert.Contains("refreshed_column", tab.Columns);
            Assert.Contains(table.Children, node => node.RawName == "refreshed_column");
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, first), candidate => candidate.Text == "refreshed_column");
            Assert.Equal(original, await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, second));

            await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"ALTER TABLE {quoted} DROP COLUMN refreshed_column;");
            await tree.RefreshNodeAsync(table);
            Assert.DoesNotContain(table.Children, node => node.RawName == "refreshed_column");
            Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, first), candidate => candidate.Text == "refreshed_column");
            await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE {quoted};");
            await tree.RefreshNodeAsync(root);
            Assert.Null(Find(root, fixture.TableName));
            Assert.Empty(await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, first));
            Assert.Equal(identity, tab.ObjectSelection.Identity);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    private static SchemaNode? Find(SchemaNode node, string name) =>
        node.Type == SchemaNodeType.Table && node.RawName == name ? node :
            node.Children.Select(child => Find(child, name)).FirstOrDefault(found => found != null);
}
