using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Data Transfer between two databases of one PostgreSQL server through a connection that shows every database
/// (data-transfer/07): the wizard's pickers open each database's own session, and the rows land in the chosen one.
/// Skipped unless DBCONNECT_CONFORMANCE_POSTGRESQL_CONNECTION_STRING is set (build/databases/run-provider-integration.ps1).
/// </summary>
public sealed class DataTransferServerDatabasesTests : MainWindowTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TablesMoveBetweenTwoDatabasesOfOneServer()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (from, to) = ($"rain_xfer_from_{suffix}", $"rain_xfer_to_{suffix}");
        var provider = new PostgreSqlProvider();
        var admin = new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ToString();
        await provider.ExecuteNonQueryAsync(admin, $"CREATE DATABASE {from}");
        await provider.ExecuteNonQueryAsync(admin, $"CREATE DATABASE {to}");
        try
        {
            await provider.ExecuteNonQueryAsync(new NpgsqlConnectionStringBuilder(admin) { Database = from }.ToString(),
                "CREATE TABLE orders (id int PRIMARY KEY, what text); INSERT INTO orders VALUES (1, 'one'), (2, 'two');");
            var vm = CreateMainWindowViewModel(new DatabaseService());
            await vm.AddConnectionAsync(new ConnectionInfo { Name = "server", Provider = "PostgreSQL", ConnectionString = admin, ShowAllDatabases = true });
            var server = vm.ActiveSessionOptions.Single(s => s.Name == "server");
            vm.Tools.OpenDataTransferCommand.Execute(null);
            var wizard = Assert.IsType<DataTransferViewModel>(vm.SelectedTab);

            wizard.SourceSession = server;
            Assert.Contains(from, wizard.SourceDatabase.Databases);
            await wizard.SourceDatabase.ChooseAsync(from);
            Assert.Equal(from, wizard.Source!.DatabaseLabel);
            await wizard.NextCommand.ExecuteAsync(null);
            wizard.TargetSession = server;
            await wizard.TargetDatabase.ChooseAsync(to);
            Assert.Equal("", wizard.TargetRefusal);
            await wizard.NextCommand.ExecuteAsync(null);
            Assert.Equal(["public.orders"], wizard.Tables.Select(t => t.Name));
            await wizard.NextCommand.ExecuteAsync(null);
            await wizard.NextCommand.ExecuteAsync(null);
            await wizard.StartCommand.ExecuteAsync(null);

            Assert.Equal("Transferred 1 of 1 table · 2 rows.", wizard.Summary);
            var copied = await provider.ExecuteQueryAsync(new NpgsqlConnectionStringBuilder(admin) { Database = to }.ToString(),
                "SELECT count(*) FROM orders");
            Assert.Equal(2L, Convert.ToInt64(copied.ObjectRows[0][0]));
            // The same database on both sides is refused.
            await wizard.TargetDatabase.ChooseAsync(from);
            Assert.Contains("same database", wizard.TargetRefusal);
            vm.Disconnect(vm.SchemaNodes.Single(n => n.Name == "server"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var database in new[] { from, to })
                await provider.ExecuteNonQueryAsync(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }
}
