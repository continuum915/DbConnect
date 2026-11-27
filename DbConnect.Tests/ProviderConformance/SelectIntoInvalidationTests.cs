using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

public sealed class SelectIntoInvalidationTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("PostgreSQL", false)]
    [InlineData("PostgreSQL", true)]
    [InlineData("SQL Server", false)]
    [InlineData("SQL Server", true)]
    public async Task SelectIntoInvalidatesDiscoveryOrRemainsPendingUntilRollback(string providerName, bool manual)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using var fixture = await ProviderConformanceFixture.CreateExternalAsync(providerName, connection);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, fixture.Provider);
        using var catalog = session.Catalog;
        var root = Assert.Single(await session.GetSchemaAsync());
        var workspace = CreateMainWindowViewModel();
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);
        workspace.ActiveSessionOptions.Add(session);
        await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session);
        var name = fixture.TableName + "_into";
        var quoted = fixture.Quote(name);
        var tab = workspace.NewQueryTabWithSession(session);
        try
        {
            tab.IsManualCommit = manual;
            tab.QueryText = $"SELECT 1 AS id INTO {quoted};";
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            if (manual)
            {
                Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
                Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text.Contains(name));
                await tab.RollbackCommand.ExecuteAsync(null);
                Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text.Contains(name));
            }
            else
                Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == name);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            await fixture.Provider.ExecuteNonQueryAsync(connection, $"DROP TABLE IF EXISTS {quoted};");
        }
    }
}
