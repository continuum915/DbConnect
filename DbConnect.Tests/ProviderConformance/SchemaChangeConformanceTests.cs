using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class SchemaChangeConformanceTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("SQLite", false, true)]
    [InlineData("DuckDB", false, true)]
    [InlineData("PostgreSQL", false, true)]
    [InlineData("SQL Server", false, true)]
    [InlineData("MySQL", false, true)]
    [InlineData("SQLite", true, true)]
    [InlineData("SQLite", true, false)]
    [InlineData("DuckDB", true, true)]
    [InlineData("DuckDB", true, false)]
    [InlineData("PostgreSQL", true, true)]
    [InlineData("PostgreSQL", true, false)]
    [InlineData("SQL Server", true, true)]
    [InlineData("SQL Server", true, false)]
    public async Task QuerySchemaChangesRespectTransactionBoundariesAndIndependentConnections(string providerName, bool manual, bool commit)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (providerName is not ("SQLite" or "DuckDB") && string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = providerName is "SQLite" or "DuckDB"
            ? await ProviderConformanceFixture.CreateLocalAsync(providerName)
            : await ProviderConformanceFixture.CreateExternalAsync(providerName, connection!);
        // DuckDB disallows ALTER when a secondary index depends on the table.
        await fixture.Provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP VIEW {fixture.QualifiedViewName};");
        if (providerName == "DuckDB")
            await fixture.Provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP INDEX {fixture.Quote(fixture.IndexName)};");
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        using var firstCatalog = first.Catalog;
        using var secondCatalog = second.Catalog;
        var workspace = CreateMainWindowViewModel();
        var root = Assert.Single(await first.GetSchemaAsync());
        workspace.ActiveSessions[root] = first;
        workspace.ActiveConnections[root] = first.Connection;
        workspace.SchemaNodes.Add(root);
        workspace.ActiveSessionOptions.Add(first);
        var identity = await first.Objects.ResolveLegacyRelationAsync(fixture.TableName);
        Assert.NotNull(identity);
        if (identity.Parent?.Kind == SchemaNodeType.Schema)
        {
            var schema = Assert.Single(root.Children, node => node.Identity == identity.Parent);
            await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(Assert.Single(schema.Children, node => node.Type == SchemaNodeType.TableFolder));
        }
        var sql = "SELECT " + ObjectNames.Quote(first.Capabilities, identity) + ".";
        await Complete(first);
        var otherCandidates = await Complete(second);
        var otherSnapshot = second.Catalog.GetSnapshot(MetadataScope.Root);
        var tab = workspace.NewQueryTabWithSession(first);
        try
        {
            tab.IsManualCommit = manual;
            tab.QueryText = $"ALTER TABLE {ObjectNames.Quote(first.Capabilities, identity)} ADD rain_pending_column INTEGER;";
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            if (manual)
            {
                Assert.True(tab.IsInTransaction);
                Assert.Equal(MetadataLoadState.Pending, first.Catalog.GetSnapshot(MetadataScope.Root).State);
                Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
                await workspace.SchemaTree.RefreshNodeAsync(root);
                Assert.DoesNotContain(await Complete(first), candidate => candidate.Text == "rain_pending_column");
                Assert.Same(otherSnapshot, second.Catalog.GetSnapshot(MetadataScope.Root));
                if (commit) await tab.CommitCommand.ExecuteAsync(null);
                else await tab.RollbackCommand.ExecuteAsync(null);
                Assert.False(tab.IsInTransaction);
            }
            Assert.Equal(!manual || commit, (await Complete(first)).Any(candidate => candidate.Text == "rain_pending_column"));
            var table = Find(root, fixture.TableName);
            Assert.NotNull(table);
            Assert.Equal(!manual || commit, table.Children.Any(node => node.RawName == "rain_pending_column"));
            Assert.Equal(otherCandidates, await Complete(second));
            Assert.Same(otherSnapshot, second.Catalog.GetSnapshot(MetadataScope.Root));
            if (!manual)
            {
                tab.QueryText = $"ALTER TABLE {ObjectNames.Quote(first.Capabilities, identity)} ADD rain_partial_column INTEGER;{(first.Capabilities.BatchDialect == SqlBatchDialect.SqlServer ? "\nGO\n" : " ")}CREATE TABLE malformed (";
                await workspace.ExecuteQueryCommand.ExecuteAsync(null);
                Assert.True(tab.CurrentResult!.HasError);
                Assert.Contains(await Complete(first), candidate => candidate.Text == "rain_partial_column");
                Assert.Contains(table.Children, node => node.RawName == "rain_partial_column");
            }
        }
        finally { await tab.ReleaseDbSessionAsync(); }
        Task<IReadOnlyList<SqlCompletionCandidate>> Complete(DatabaseSession session) =>
            CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, session);
    }

    private static SchemaNode? Find(SchemaNode node, string name) => node.RawName == name && node.Type == SchemaNodeType.Table
        ? node : node.Children.Select(child => Find(child, name)).FirstOrDefault(found => found != null);
}
