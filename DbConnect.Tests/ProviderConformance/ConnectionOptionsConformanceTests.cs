using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Connection options and connection loss against live servers (§1.5).</summary>
public sealed class ConnectionOptionsConformanceTests : MainWindowTestBase
{
    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab, DatabaseSession Session)> TabOn(string provider, string cs, ConnectionOptions options)
    {
        var established = await new ConnectionEstablisher(new DatabaseService()).EstablishAsync(
            new ConnectionInfo { Name = "live", Provider = provider, ConnectionString = cs, Options = options });
        var vm = CreateMainWindowViewModel(new DatabaseService());
        vm.Tabs.Clear();
        var tab = new QueryTabViewModel();
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        vm.ActiveSessionOptions.Add(established.Session);
        tab.SelectedSession = established.Session;
        return (vm, tab, established.Session);
    }

    private static async Task<QueryResult> Run(MainWindowViewModel vm, QueryTabViewModel tab, string sql)
    {
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        return tab.CurrentResult!;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_ReadOnlySearchPathAndStartupSql_AreAppliedByTheServer()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = "rain_opt_" + Guid.NewGuid().ToString("N")[..8];
        var provider = new PostgreSqlProvider();
        await provider.ExecuteNonQueryAsync(cs, $"CREATE SCHEMA {schema}; CREATE TABLE {schema}.items (id int); INSERT INTO {schema}.items VALUES (1)");
        try
        {
            var (vm, tab, session) = await TabOn("PostgreSQL", cs, new ConnectionOptions
            {
                IsReadOnly = true,
                DefaultSchema = schema,
                StartupSql = "SET application_name = 'rain-startup'"
            });
            var read = await Run(vm, tab, "SELECT count(*), current_setting('application_name') FROM items");
            Assert.False(read.HasError, read.ErrorMessage);
            Assert.Equal("1|rain-startup", string.Join("|", read.ObjectRows[0].Select(Convert.ToString)));

            Assert.Contains("read-only connection", (await Run(vm, tab, "DELETE FROM items")).Error!.Summary);
            var server = await Assert.ThrowsAnyAsync<Exception>(() =>
                session.Provider.ExecuteNonQueryAsync(session.ConnectionString, "DELETE FROM items"));
            Assert.Contains("read-only", server.Message);
        }
        finally { await provider.ExecuteNonQueryAsync(cs, $"DROP SCHEMA {schema} CASCADE"); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MySql_ReadOnlySessionIsEnforcedByTheServer()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var table = "rain_opt_" + Guid.NewGuid().ToString("N")[..8];
        var provider = new MySqlProvider();
        await provider.ExecuteNonQueryAsync(cs, $"CREATE TABLE {table} (id int)");
        try
        {
            var (vm, tab, session) = await TabOn("MySQL", cs, new ConnectionOptions { IsReadOnly = true });
            var opened = await tab.GetOrOpenDbSessionAsync();
            var result = await opened.ExecuteQueryAsync($"INSERT INTO {table} VALUES (1)");
            Assert.True(result.HasError);
            Assert.Contains("READ ONLY", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { await provider.ExecuteNonQueryAsync(cs, $"DROP TABLE {table}"); }
    }

    public static TheoryData<string, string, string> Killers => new()
    {
        { "PostgreSQL", "SELECT pg_backend_pid()", "SELECT pg_terminate_backend({0})" },
        { "MySQL", "SELECT CONNECTION_ID()", "KILL {0}" },
        { "SQL Server", "SELECT @@SPID", "KILL {0}" }
    };

    [Theory]
    [MemberData(nameof(Killers))]
    [Trait("Category", "Integration")]
    public async Task KilledSession_ShowsTheLossAndTheLostTransaction_AndReconnectRunsNothingAgain(
        string providerName, string whoAmI, string kill)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var provider = new DatabaseService().GetProvider(providerName);
        var table = "rain_loss_" + Guid.NewGuid().ToString("N")[..8];
        await provider.ExecuteNonQueryAsync(cs, $"CREATE TABLE {table} (id int)");
        QueryTabViewModel? opened = null;
        try
        {
            var (vm, tab, _) = await TabOn(providerName, cs, new ConnectionOptions());
            opened = tab;
            tab.IsManualCommit = true;
            var id = Convert.ToString((await Run(vm, tab, whoAmI)).ObjectRows[0][0]);
            Assert.False((await Run(vm, tab, $"INSERT INTO {table} VALUES (1)")).HasError);
            Assert.True(tab.IsInTransaction);

            await provider.ExecuteNonQueryAsync(cs, string.Format(kill, id));
            await Task.Delay(500);
            var failed = await Run(vm, tab, "SELECT 1");
            Assert.True(failed.HasError);
            Assert.True(tab.ConnectionLoss.IsLost);
            Assert.Contains("rolled back by the server", tab.ConnectionLoss.Message);

            var blocked = await Run(vm, tab, "SELECT 2");
            Assert.Contains("Reconnect", blocked.ErrorMessage);

            await tab.ConnectionLoss.ReconnectCommand.ExecuteAsync(null);
            Assert.False(tab.ConnectionLoss.IsLost, tab.ConnectionLoss.Message);
            var count = await Run(vm, tab, $"SELECT count(*) FROM {table}");
            Assert.False(count.HasError, count.ErrorMessage);
            Assert.Equal(0L, Convert.ToInt64(count.ObjectRows[0][0]));
        }
        finally
        {
            if (opened != null) await opened.ReleaseDbSessionAsync();
            await provider.ExecuteNonQueryAsync(cs, $"DROP TABLE {table}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_TableTabShowsTheLossAndReconnectReloadsThePage()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var table = "rain_tabloss_" + Guid.NewGuid().ToString("N")[..8];
        var provider = new PostgreSqlProvider();
        await provider.ExecuteNonQueryAsync(cs, $"CREATE TABLE public.{table} (id int PRIMARY KEY); INSERT INTO public.{table} VALUES (1), (2)");
        TableDataTabViewModel? tab = null;
        try
        {
            var established = await new ConnectionEstablisher(new DatabaseService()).EstablishAsync(
                new ConnectionInfo { Name = "live", Provider = "PostgreSQL", ConnectionString = cs });
            var root = Assert.Single(established.Schema);
            var owner = Assert.Single(root.Children, n => n.RawName == "public");
            var node = Assert.Single(await established.Session.Objects.GetChildrenAsync(owner.Identity!, SchemaNodeType.Table),
                n => n.RawName == table);
            tab = new TableDataTabViewModel(table, established.Session, node);
            await tab.RefreshInternalAsync(bypassDirtyCheck: true);
            Assert.True(tab.Rows.Count == 2, tab.StatusMessage);

            var session = await tab.GetOrOpenDbSessionAsync();
            var pid = Convert.ToString((await session.ExecuteQueryAsync("SELECT pg_backend_pid()")).ObjectRows[0][0]);
            await provider.ExecuteNonQueryAsync(cs, $"SELECT pg_terminate_backend({pid})");
            await Task.Delay(500);
            await tab.RefreshInternalAsync(bypassDirtyCheck: true);
            Assert.True(tab.ConnectionLoss.IsLost);

            await tab.ConnectionLoss.ReconnectCommand.ExecuteAsync(null);
            Assert.False(tab.ConnectionLoss.IsLost, tab.ConnectionLoss.Message);
            Assert.Equal(2, tab.Rows.Count);
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            await provider.ExecuteNonQueryAsync(cs, $"DROP TABLE public.{table}");
        }
    }
}
