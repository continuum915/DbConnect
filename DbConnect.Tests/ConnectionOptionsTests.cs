using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Connection options: auth, timeouts, read-only, session setup, extra properties (§1.5).</summary>
public sealed class ConnectionOptionsTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-options-" + Guid.NewGuid().ToString("N"));

    public ConnectionOptionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static string Apply(string provider, string cs, ConnectionOptions options) =>
        ConnectionOptionDialects.For(provider)!.Apply(cs, options);

    [Fact]
    public void PostgreSql_TranslatesTimeoutsSchemaReadOnlyAndProperties()
    {
        var cs = Apply("PostgreSQL", "Host=h;Username=u;Password=p", new ConnectionOptions
        {
            ConnectTimeoutSeconds = 7,
            CommandTimeoutSeconds = 90,
            KeepAliveSeconds = 15,
            DefaultSchema = "sales",
            IsReadOnly = true,
            AdditionalProperties = [new() { Key = "Application Name", Value = "DbConnect;test" }]
        });
        var builder = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal(7, builder.Timeout);
        Assert.Equal(90, builder.CommandTimeout);
        Assert.Equal(15, builder.KeepAlive);
        Assert.Equal("sales", builder.SearchPath);
        Assert.Contains("default_transaction_read_only=on", builder.Options);
        Assert.Equal("DbConnect;test", builder.ApplicationName);
        Assert.Equal("p", builder.Password);

        var integrated = new NpgsqlConnectionStringBuilder(Apply("PostgreSQL", "Host=h;Username=u;Password=p",
            new ConnectionOptions { Authentication = ConnectionAuthentication.Integrated }));
        Assert.Null(integrated.Password);
    }

    [Theory]
    [InlineData(ConnectionAuthentication.Integrated, null, true)]
    [InlineData(ConnectionAuthentication.EntraInteractive, SqlAuthenticationMethod.ActiveDirectoryInteractive, false)]
    [InlineData(ConnectionAuthentication.EntraIntegrated, SqlAuthenticationMethod.ActiveDirectoryIntegrated, false)]
    [InlineData(ConnectionAuthentication.EntraPassword, SqlAuthenticationMethod.ActiveDirectoryPassword, false)]
    [InlineData(ConnectionAuthentication.EntraServicePrincipal, SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, false)]
    [InlineData(ConnectionAuthentication.EntraManagedIdentity, SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, false)]
    [InlineData(ConnectionAuthentication.EntraDefault, SqlAuthenticationMethod.ActiveDirectoryDefault, false)]
    public void SqlServer_TranslatesAuthenticationModes(ConnectionAuthentication mode, SqlAuthenticationMethod? method, bool integrated)
    {
        var builder = new SqlConnectionStringBuilder(Apply("SQL Server", "Server=s;User ID=u;Password=p",
            new ConnectionOptions { Authentication = mode, ConnectTimeoutSeconds = 9, IsReadOnly = true }));
        Assert.Equal(integrated, builder.IntegratedSecurity);
        if (method is { } expected) Assert.Equal(expected, builder.Authentication);
        Assert.Equal(9, builder.ConnectTimeout);
        Assert.Equal(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal(mode is ConnectionAuthentication.EntraPassword or ConnectionAuthentication.EntraServicePrincipal ? "p" : "",
            builder.Password);
    }

    [Fact]
    public void MySql_TranslatesTimeoutsAndRunsReadOnlyPerSession()
    {
        var options = new ConnectionOptions { ConnectTimeoutSeconds = 4, CommandTimeoutSeconds = 70, KeepAliveSeconds = 20, IsReadOnly = true, StartupSql = "SET @a = 1; SET @b = 2" };
        var builder = new MySqlConnectionStringBuilder(Apply("MySQL", "Server=h;User ID=u", options));
        Assert.Equal(4u, builder.ConnectionTimeout);
        Assert.Equal(70u, builder.DefaultCommandTimeout);
        Assert.Equal(20u, builder.Keepalive);
        Assert.Equal(["SET SESSION TRANSACTION READ ONLY", "SET @a = 1", "SET @b = 2"],
            ConnectionOptionDialects.For("MySQL")!.SessionSetup(options, SqlBatchDialect.MySql));
    }

    [Fact]
    public void UnknownAndSecretProperties_AreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Apply("PostgreSQL", "Host=h",
            new ConnectionOptions { AdditionalProperties = [new() { Key = "No Such Keyword", Value = "1" }] }));
        var secret = Assert.Throws<InvalidOperationException>(() => Apply("MySQL", "Server=h",
            new ConnectionOptions { AdditionalProperties = [new() { Key = "Pwd", Value = "x" }] }));
        Assert.Contains("password field", secret.Message);

        var dialog = new ConnectionOptionsViewModel();
        dialog.SetProvider("PostgreSQL");
        dialog.AdditionalProperties = "Application Name=DbConnect\nclient_secret=abc";
        Assert.Contains("secret", dialog.Error);
        dialog.AdditionalProperties = "Application Name=DbConnect";
        dialog.ConnectTimeout = "soon";
        Assert.Contains("whole number", dialog.Error);
    }

    [Fact]
    public void Dialog_ShowsOnlyTheProvidersOptions_AndRoundTripsThem()
    {
        var dialog = new ConnectionDialogViewModel();
        dialog.SelectedProvider = "SQL Server";
        Assert.True(dialog.Options.HasAuthenticationChoice);
        Assert.False(dialog.Options.SupportsDefaultSchema);
        dialog.Options.Authentication = dialog.Options.AuthenticationChoices.Single(c => c.Mode == ConnectionAuthentication.Integrated);
        Assert.False(dialog.Options.UsesPassword);
        Assert.False(dialog.Options.UsesUsername);

        dialog.SelectedProvider = "MySQL";
        Assert.False(dialog.Options.HasAuthenticationChoice);
        Assert.True(dialog.Options.UsesPassword);
        dialog.SelectedProvider = "DuckDB";
        Assert.True(dialog.Options.SupportsDefaultSchema);
        Assert.False(dialog.Options.SupportsConnectTimeout);

        dialog.SelectedProvider = "PostgreSQL";
        var saved = new ConnectionOptions
        {
            Authentication = ConnectionAuthentication.Integrated,
            ConnectTimeoutSeconds = 5,
            CommandTimeoutSeconds = 60,
            KeepAliveSeconds = 30,
            IsReadOnly = true,
            DefaultSchema = "app",
            StartupSql = "SET work_mem = '64MB'",
            AdditionalProperties = [new() { Key = "Application Name", Value = "DbConnect" }]
        };
        dialog.Options.Load(saved);
        var round = dialog.Options.ToOptions();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(saved), System.Text.Json.JsonSerializer.Serialize(round));
    }

    [Fact]
    public async Task SavedConnections_KeepOptionsAndEnvironmentTier()
    {
        var store = new OptionsCredentialStore();
        var settings = new ConnectionSettingsService(store, _directory);
        await settings.SaveConnectionsAsync([new ConnectionInfo
        {
            Name = "c", Provider = "PostgreSQL", ConnectionString = "Host=h;Username=u;Password=secret", EnvironmentTier = "Production",
            Options = new ConnectionOptions { IsReadOnly = true, StartupSql = "SET x = 1", ConnectTimeoutSeconds = 3 }
        }]);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Directory.GetFiles(_directory, "connections-meta.json").Single()));

        var loaded = Assert.Single(await new ConnectionSettingsService(store, _directory).LoadConnectionsAsync());
        Assert.Equal("Production", loaded.EnvironmentTier);
        Assert.True(loaded.Options.IsReadOnly);
        Assert.Equal("SET x = 1", loaded.Options.StartupSql);
        Assert.Equal(3, loaded.Options.ConnectTimeoutSeconds);
    }

    [Fact]
    public async Task ShowAllDatabases_DefaultsOnForServers_IsHiddenForFiles_AndIsSaved()
    {
        var dialog = new ConnectionDialogViewModel();
        dialog.SelectedProvider = "PostgreSQL";
        Assert.True(dialog.SupportsServerDatabases);
        Assert.True(dialog.ShowAllDatabases);
        dialog.SelectedProvider = "SQLite";
        Assert.False(dialog.SupportsServerDatabases);

        var store = new OptionsCredentialStore();
        var settings = new ConnectionSettingsService(store, _directory);
        await settings.SaveConnectionsAsync([
            new ConnectionInfo { Name = "server", Provider = "MySQL", ConnectionString = "Server=h", ShowAllDatabases = true },
            new ConnectionInfo { Name = "pinned", Provider = "MySQL", ConnectionString = "Server=h" }]);
        var loaded = await new ConnectionSettingsService(store, _directory).LoadConnectionsAsync();
        Assert.True(loaded.Single(c => c.Name == "server").ShowAllDatabases);
        Assert.False(loaded.Single(c => c.Name == "pinned").ShowAllDatabases);
    }

    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab, DatabaseSession Session)> TabOn(ConnectionInfo connection)
    {
        var established = await new ConnectionEstablisher(new DatabaseService()).EstablishAsync(connection);
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
    public async Task Sqlite_ReadOnlyConnection_RefusesWritesInDbConnectAndOpensTheFileReadOnly()
    {
        var path = Path.Combine(_directory, "ro.db");
        await using (var setup = new SqliteConnection($"Data Source={path}"))
        {
            setup.Open();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();
        }
        var connection = new ConnectionInfo
        {
            Name = "ro",
            Provider = "SQLite",
            ConnectionString = $"Data Source={path};Pooling=False",
            Options = new ConnectionOptions { IsReadOnly = true, CommandTimeoutSeconds = 77 }
        };
        var (vm, tab, session) = await TabOn(connection);
        Assert.Equal(77, tab.TimeoutSeconds);

        var refused = await Run(vm, tab, "SELECT 1; DELETE FROM t;");
        Assert.Contains("read-only connection", refused.Error!.Summary);
        Assert.Equal(1L, new SqliteConnection($"Data Source={path}").Let(c => { c.Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM t"; return q.ExecuteScalar(); }));

        var read = await Run(vm, tab, "SELECT count(*) FROM t");
        Assert.False(read.HasError, read.ErrorMessage);

        // The server side refuses too: the provider's own connection string is read-only.
        await Assert.ThrowsAnyAsync<Exception>(() => session.Provider.ExecuteNonQueryAsync(session.ConnectionString, "DELETE FROM t"));
    }

    [Fact]
    public async Task DuckDb_DefaultSchemaAndStartupSql_ApplyToEachNewSession()
    {
        var path = Path.Combine(_directory, "s.duckdb");
        await using (var setup = new DuckDBConnection($"Data Source={path}"))
        {
            setup.Open();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = "CREATE SCHEMA app; CREATE TABLE app.items (id INTEGER); INSERT INTO app.items VALUES (1), (2);";
            cmd.ExecuteNonQuery();
        }
        var connection = new ConnectionInfo
        {
            Name = "duck",
            Provider = "DuckDB",
            ConnectionString = $"Data Source={path}",
            Options = new ConnectionOptions { DefaultSchema = "app", StartupSql = "SET threads = 1; CREATE TEMP TABLE startup_marker AS SELECT 42 AS v;" }
        };
        var (vm, tab, _) = await TabOn(connection);

        var result = await Run(vm, tab, "SELECT (SELECT count(*) FROM items), (SELECT v FROM startup_marker), current_setting('threads')");
        Assert.False(result.HasError, result.ErrorMessage);
        Assert.Equal("2|42|1", string.Join("|", result.ObjectRows[0].Select(Convert.ToString)));
    }

    [Fact]
    public async Task FailingStartupSql_FailsTheOpenAndNamesTheStatement()
    {
        var path = Path.Combine(_directory, "bad.db");
        var connection = new ConnectionInfo
        {
            Name = "bad",
            Provider = "SQLite",
            ConnectionString = $"Data Source={path};Pooling=False",
            Options = new ConnectionOptions { StartupSql = "SELECT 1; SELECT * FROM no_such_table;" }
        };
        var (vm, tab, _) = await TabOn(connection);

        var result = await Run(vm, tab, "SELECT 1");
        Assert.True(result.HasError);
        Assert.Contains("SELECT * FROM no_such_table", result.Error!.Detail ?? result.ErrorMessage);
    }
}

internal static class TestFunctional
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) where T : IDisposable
    {
        using (value) return map(value);
    }
}

internal sealed class OptionsCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();
    public Task SetAsync(string key, string secret) { _store[key] = secret; return Task.CompletedTask; }
    public Task<string?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var v) ? v : (string?)null);
    public Task DeleteAsync(string key) { _store.Remove(key); return Task.CompletedTask; }
}
