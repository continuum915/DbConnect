using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Browsing every database on a server and switching a tab between them (§1.2).</summary>
public sealed class ServerDatabaseBrowsingTests : MainWindowTestBase
{
    private static IEnumerable<SchemaNode> DatabaseNodes(SchemaNode root) =>
        root.Children.SelectMany(c => c.Type == SchemaNodeType.DatabaseFolder ? c.Children : [c])
            .Where(c => c.Type == SchemaNodeType.Database);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_ListsDatabases_OpensThemLazily_AndTabsSwitchBetweenThem()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var open = $"rain_db_open_{suffix}";
        var closed = $"rain_db_closed_{suffix}";
        var role = $"rain_limited_{suffix}";
        var provider = new PostgreSqlProvider();
        var admin = new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ToString();
        await provider.ExecuteNonQueryAsync(admin, $"CREATE DATABASE {open}");
        await provider.ExecuteNonQueryAsync(admin, $"CREATE DATABASE {closed}");
        try
        {
            await provider.ExecuteNonQueryAsync(admin, $"""
                CREATE ROLE {role} LOGIN PASSWORD 'limited_pw';
                REVOKE CONNECT ON DATABASE {closed} FROM PUBLIC;
                """);
            await provider.ExecuteNonQueryAsync(new NpgsqlConnectionStringBuilder(admin) { Database = open }.ToString(),
                $"CREATE SCHEMA sales; CREATE TABLE sales.orders (id int PRIMARY KEY); INSERT INTO sales.orders VALUES (1), (2); " +
                $"GRANT USAGE ON SCHEMA sales TO {role}; GRANT SELECT ON sales.orders TO {role};");
            var limited = new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = "limited_pw" }.ToString();

            var vm = CreateMainWindowViewModel(new DatabaseService());
            await vm.AddConnectionAsync(new ConnectionInfo
            {
                Name = "server",
                Provider = "PostgreSQL",
                ConnectionString = limited,
                ShowAllDatabases = true
            });
            var root = vm.SchemaNodes.Single(n => n.Name == "server");
            var databases = DatabaseNodes(root).ToList();
            var defaultNode = databases.Single(n => n.RawName == "rain_ecommerce");
            Assert.True(defaultNode.IsLoaded);
            Assert.Contains(root.Children, c => c.Type == SchemaNodeType.DatabaseFolder &&
                c.Children.Any(d => d.RawName == "template1"));
            var closedNode = databases.Single(n => n.RawName == closed);
            Assert.False(closedNode.IsLazyLoadable);
            Assert.Contains("no CONNECT privilege", closedNode.Name);

            // Expanding a database opens its own session and catalog.
            var openNode = databases.Single(n => n.RawName == open);
            Assert.Null(vm.SchemaTree.FindSessionForNode(openNode));
            await vm.LoadSchemaNodeChildrenAsync(openNode);
            var openSession = vm.SchemaTree.FindSessionForNode(openNode);
            Assert.NotNull(openSession);
            Assert.Equal(open, openSession!.DatabaseLabel);
            Assert.True(openNode.Children.Any(c => c.RawName == "sales"),
                string.Join(",", openNode.Children.Select(c => $"{c.Type}:{c.RawName}:{c.Name}[{string.Join(";", c.Children.Take(5).Select(g => g.Type + ":" + g.RawName))}]")));
            Assert.Same(vm.SchemaTree.FindSessionForNode(defaultNode), vm.SchemaTree.FindSessionForNode(root));

            // A tab on the default database switches to the other one through the picker.
            await vm.Connections.NewQueryTabAsync(defaultNode);
            var tab = Assert.IsType<QueryTabViewModel>(vm.SelectedTab);
            Assert.Equal("rain_ecommerce", tab.DatabasePicker.SelectedDatabase);
            Assert.Contains(open, tab.DatabasePicker.Databases);
            Assert.DoesNotContain(closed, tab.DatabasePicker.Databases);
            Assert.Contains($"/ rain_ecommerce", tab.SelectedSession!.DisplayName);

            tab.DatabasePicker.SelectedDatabase = open;
            await WaitFor(() => tab.SelectedSession == openSession);
            tab.QueryText = "SELECT count(*) FROM sales.orders";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.Equal(2L, Convert.ToInt64(tab.CurrentResult!.ObjectRows[0][0]));

            // Switching is refused while a transaction is open.
            tab.IsManualCommit = true;
            tab.QueryText = "SELECT 1";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.True(tab.IsInTransaction);
            await tab.DatabasePicker.SwitchAsync(tab.SelectedSession!.Server!, "rain_ecommerce");
            Assert.Same(openSession, tab.SelectedSession);
            Assert.Contains("transaction", tab.DatabasePicker.Notice);
            await tab.ReleaseDbSessionAsync();

            // Closing one database releases its session; disconnecting the server releases the rest.
            vm.Disconnect(openNode);
            Assert.Null(vm.SchemaTree.FindSessionForNode(openNode));
            Assert.DoesNotContain(openSession, vm.ActiveSessionOptions);
            Assert.False(openNode.IsLoaded);
            vm.Disconnect(root);
            Assert.DoesNotContain(root, vm.SchemaNodes);
            Assert.Empty(vm.ActiveSessions);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await provider.ExecuteNonQueryAsync(admin, $"DROP DATABASE IF EXISTS {open} WITH (FORCE)");
            await provider.ExecuteNonQueryAsync(admin, $"DROP DATABASE IF EXISTS {closed} WITH (FORCE)");
            await provider.ExecuteNonQueryAsync(admin, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }

    [Theory]
    [InlineData("SQL Server", "master", "USE master")]
    [InlineData("MySQL", "information_schema", "USE information_schema")]
    [Trait("Category", "Integration")]
    public async Task ListsDatabasesAndTracksUse(string providerName, string systemDatabase, string use)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var vm = CreateMainWindowViewModel(new DatabaseService());
        await vm.AddConnectionAsync(new ConnectionInfo { Name = "server", Provider = providerName, ConnectionString = cs, ShowAllDatabases = true });
        var root = vm.SchemaNodes.Single(n => n.Name == "server");
        var system = Assert.Single(root.Children, c => c.Type == SchemaNodeType.DatabaseFolder);
        Assert.Contains(system.Children, d => d.RawName == systemDatabase);
        var defaultNode = DatabaseNodes(root).Single(n => n.IsLoaded);
        Assert.Equal("rain_ecommerce", defaultNode.RawName);

        await vm.Connections.NewQueryTabAsync(defaultNode);
        var tab = Assert.IsType<QueryTabViewModel>(vm.SelectedTab);
        tab.QueryText = use;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.Equal(systemDatabase, tab.DatabasePicker.InSessionDatabase);
        await tab.ReleaseDbSessionAsync();
        vm.Disconnect(root);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PinnedConnection_KeepsTodaysShape()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var vm = CreateMainWindowViewModel(new DatabaseService());
        await vm.AddConnectionAsync(new ConnectionInfo { Name = "pinned", Provider = "PostgreSQL", ConnectionString = cs });
        var root = vm.SchemaNodes.Single(n => n.Name == "pinned");
        Assert.Contains(root.Children, c => c.Type == SchemaNodeType.Schema && c.RawName == "public");
        Assert.DoesNotContain(root.Children, c => c.Type is SchemaNodeType.Database or SchemaNodeType.DatabaseFolder);
        Assert.False(vm.SchemaTree.FindSessionForNode(root)!.Server != null);
        vm.Disconnect(root);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
