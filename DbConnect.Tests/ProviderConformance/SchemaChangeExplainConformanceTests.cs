using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class SchemaChangeExplainConformanceTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("create", false)]
    [InlineData("/* alias */ create", true)]
    [InlineData("into", true)]
    [InlineData("comment", false)]
    [Trait("Category", "Integration")]
    public async Task OrdinarySelectKeywordAliasesDoNotRefreshOrSuspendMetadata(string alias, bool manual)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, new PostgreSqlProvider());
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        try
        {
            await session.GetSchemaAsync();
            var before = session.Catalog.GetSnapshot(MetadataScope.Root);
            tab.IsManualCommit = manual;
            tab.QueryText = $"SELECT 1 AS {alias};";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            Assert.Equal(before, session.Catalog.GetSnapshot(MetadataScope.Root));
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
        }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ExplainAnalyzeCreateTableRefreshesOrDefersMetadataWithItsTransaction(bool manual)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, new PostgreSqlProvider());
        var name = "rain_explain_" + Guid.NewGuid().ToString("N")[..12];
        var quoted = ObjectNames.Quote(session.Capabilities, new(null, "public", name, SchemaNodeType.Table));
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        try
        {
            var root = Assert.Single(await session.GetSchemaAsync());
            vm.ActiveConnections[root] = session.Connection;
            vm.ActiveSessions[root] = session;
            vm.SchemaNodes.Add(root);
            var schema = Assert.Single(root.Children, node => node.RawName == "public");
            var folder = Assert.Single(schema.Children, node => node.Type == SchemaNodeType.TableFolder);
            await vm.SchemaTree.LoadSchemaNodeChildrenAsync(folder);
            async Task<IReadOnlyList<SqlCompletionCandidate>> Complete() =>
                await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM public.", 21, session);
            Assert.DoesNotContain(await Complete(), candidate => candidate.Text == name);

            tab.IsManualCommit = manual;
            tab.QueryText = $"EXPLAIN ANALYZE CREATE TABLE {quoted} AS SELECT 1 AS id;";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            if (manual)
            {
                Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
                Assert.Contains("transaction", root.MetadataStatus, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(await Complete(), candidate => candidate.Text == name);
                await tab.CommitCommand.ExecuteAsync(null);
            }
            Assert.Contains(await Complete(), candidate => candidate.Text == name);
            Assert.Contains(folder.Children, node => node.RawName == name);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {quoted};");
        }
    }
}
