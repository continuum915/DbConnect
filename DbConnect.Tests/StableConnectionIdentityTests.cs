using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class StableConnectionIdentityTests
{
    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    public async Task CertificatePathsPersistWithoutReadingPrivateKeyContents(string provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var keyPath = Path.Combine(directory, "client.key");
            await File.WriteAllTextAsync(keyPath, "PRIVATE KEY CONTENT MUST NOT BE STORED");
            var settings = new ConnectionSettingsService(new MemoryCredentialStore(), directory);
            var vm = new DbConnect.ViewModels.ConnectionDialogViewModel(new DatabaseService(), settings)
            {
                SelectedProvider = provider,
                ConnectionName = "TLS",
                Host = "db",
                SslMode = "VerifyFull",
                SslCaCertPath = "ca.pem",
                SslClientCertPath = "client.pem",
                SslClientKeyPath = keyPath
            };
            await vm.SaveConnectionCommand.ExecuteAsync(null);
            var loaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(keyPath, loaded.SslClientKeyPath);
            Assert.Equal(keyPath, ConnectionStringFormats.For(provider)!.Parse(loaded.ConnectionString).SslClientKeyPath);
            var editor = new DbConnect.ViewModels.ConnectionDialogViewModel(new DatabaseService(), settings) { SelectedSavedConnection = loaded };
            await editor.Initialization;
            Assert.Equal(keyPath, editor.SslClientKeyPath);
            Assert.Equal("VerifyFull", editor.SslMode);
            loaded.SslClientKeyPath = null;
            loaded.SslClientCertPath = null;
            loaded.SslCaCertPath = null;
            editor.SelectedSavedConnection = null;
            editor.SelectedSavedConnection = loaded;
            Assert.Equal(keyPath, editor.SslClientKeyPath);
            Assert.Equal("client.pem", editor.SslClientCertPath);
            Assert.Equal("ca.pem", editor.SslCaCertPath);
            var metadata = await File.ReadAllTextAsync(Path.Combine(directory, "connections-meta.json"));
            Assert.DoesNotContain("PRIVATE KEY CONTENT", metadata);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{invalid-json")]
    [InlineData("{\"HistoryByConnection\":null}")]
    [InlineData("{\"ColumnLayouts\":null}")]
    public async Task CorruptWorkspaceDoesNotHideSavedConnectionsOrOverwriteWorkspace(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-corrupt-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new ConnectionSettingsService(new MemoryCredentialStore(), directory);
            var connection = new ConnectionInfo { Name = "Local", ConnectionString = "Data Source=a.db" };
            await settings.SaveConnectionsAsync([connection]);
            var path = Path.Combine(directory, "query-workspace.json");
            await File.WriteAllTextAsync(path, contents);
            var loaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(connection.Id, loaded.Id);
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path,
                """{"HistoryByConnection":{"F552BE1A2BA8AAC8E77E76A92EB92B6B0E5050AADE925DEA5148C710C63C45EE":[{"Sql":"SELECT 31"}]}}""");
            loaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(connection.Id, loaded.Id);
            Assert.Equal("SELECT 31", Assert.Single(await new QueryWorkspaceService(path).LoadHistoryAsync(loaded)).Sql);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("query-workspace.json")]
    [InlineData("last-session.json")]
    public async Task LockedAuxiliaryFileDoesNotHideSavedConnections(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-locked-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new ConnectionSettingsService(new MemoryCredentialStore(), directory);
            var connection = new ConnectionInfo { Name = "Local" };
            await settings.SaveConnectionsAsync([connection]);
            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, "{}");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var loaded = Assert.Single(await settings.LoadConnectionsAsync());
                Assert.Equal(connection.Id, loaded.Id);
            }
            Assert.Equal("{}", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DeletingConnectionRemovesOnlyItsWorkspaceAndSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MemoryCredentialStore();
            var settings = new ConnectionSettingsService(store, directory);
            var removed = new ConnectionInfo { Name = "Same", Provider = "PostgreSQL", ConnectionString = "Host=old;Password=secret" };
            var kept = new ConnectionInfo { Name = "Same", Provider = "PostgreSQL", ConnectionString = "Host=new;Password=other" };
            await settings.SaveConnectionsAsync([removed, kept]);
            var workspace = new QueryWorkspaceService(Path.Combine(directory, "query-workspace.json"));
            foreach (var connection in new[] { removed, kept })
            {
                await workspace.AddHistoryEntryAsync(connection, new QueryHistoryEntry { Sql = "SELECT 9" });
                await workspace.SaveTableColumnLayoutAsync(connection, "Customers", new TableColumnLayout { Order = ["Id"] });
            }
            await workspace.SaveTimeoutSecondsAsync(71);
            await settings.DeleteConnectionAsync(removed);
            await settings.SaveConnectionsAsync([kept]);
            Assert.Empty(await workspace.LoadHistoryAsync(removed));
            Assert.Empty((await workspace.LoadTableColumnLayoutAsync(removed, "Customers")).Order);
            Assert.Single(await workspace.LoadHistoryAsync(kept));
            Assert.Equal(["Id"], (await workspace.LoadTableColumnLayoutAsync(kept, "Customers")).Order);
            Assert.Equal(71, await workspace.LoadTimeoutSecondsAsync());
            Assert.Null(await store.GetAsync($"rain.conn.{removed.Id}.password"));
            var reloaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(kept.Id, reloaded.Id);
            Assert.Contains("Password=other", reloaded.ConnectionString);
            Assert.DoesNotContain(removed.Id.ToString(), await File.ReadAllTextAsync(Path.Combine(directory, "query-workspace.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LegacySecretsSurviveRenameAndCanBeCleared()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MemoryCredentialStore();
            await store.SetAsync("rain.conn.Before.password", "before");
            await store.SetAsync("rain.conn.Before.sshpassword", "ssh-secret");
            await store.SetAsync("rain.conn.Before.sshkeyphrase", "key-secret");
            await File.WriteAllTextAsync(Path.Combine(directory, "connections-meta.json"), """
                [{"Name":"Before","Provider":"PostgreSQL","ConnectionStringNoPassword":"Host=old;Username=user",
                  "SshTunnel":{"Enabled":true}}]
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "query-workspace.json"), """
                {"HistoryByConnection":{"560DE56AF2D9048B19C12CC9A5F3A208173569235601F0CEB7AAC65253870659":[{"Sql":"SELECT 23"}]}}
                """);
            var settings = new ConnectionSettingsService(store, directory);
            var connection = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Contains("Password=before", connection.ConnectionString);
            connection.Name = "After";
            await settings.SaveConnectionsAsync([connection]);
            var reloaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Contains("Password=before", reloaded.ConnectionString);
            Assert.Equal("SELECT 23", Assert.Single(await new QueryWorkspaceService(
                Path.Combine(directory, "query-workspace.json")).LoadHistoryAsync(reloaded)).Sql);
            Assert.Equal("ssh-secret", reloaded.SshTunnel!.SshPassword);
            Assert.Equal("key-secret", reloaded.SshTunnel.PrivateKeyPassphrase);
            reloaded.ConnectionString = "Host=new;Username=user;Password=after";
            reloaded.SshTunnel.SshPassword = string.Empty;
            reloaded.SshTunnel.PrivateKeyPassphrase = string.Empty;
            await settings.SaveConnectionsAsync([reloaded]);
            reloaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Contains("Host=new", reloaded.ConnectionString);
            Assert.Contains("Password=after", reloaded.ConnectionString);
            Assert.Empty(reloaded.SshTunnel!.SshPassword);
            Assert.Empty(reloaded.SshTunnel.PrivateKeyPassphrase);
            Assert.Null(await store.GetAsync("rain.conn.Before.password"));
            Assert.Null(await store.GetAsync("rain.conn.Before.sshpassword"));
            Assert.Null(await store.GetAsync("rain.conn.Before.sshkeyphrase"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SavedConnectionsAcquirePersistentIdsAndMigrateWorkspaceOnLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-connections-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "connections-meta.json"),
                """[{"Name":"Local","Provider":"SQLite","ConnectionStringNoPassword":"Data Source=a.db"}]""");
            await File.WriteAllTextAsync(Path.Combine(directory, "query-workspace.json"),
                """{"HistoryByConnection":{"F552BE1A2BA8AAC8E77E76A92EB92B6B0E5050AADE925DEA5148C710C63C45EE":[{"Sql":"SELECT 17"}]}}""");
            var settings = new ConnectionSettingsService(new MemoryCredentialStore(), directory);
            var connection = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.NotEqual(Guid.Empty, connection.Id);
            var reloaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(connection.Id, reloaded.Id);
            connection.Name = "Renamed";
            connection.ConnectionString = "Data Source=b.db";
            await settings.SaveConnectionsAsync([connection]);
            reloaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(connection.Id, reloaded.Id);
            Assert.Equal("SELECT 17", Assert.Single(await new QueryWorkspaceService(
                Path.Combine(directory, "query-workspace.json")).LoadHistoryAsync(reloaded)).Sql);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public Task SetAsync(string key, string secret) { _secrets[key] = secret; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key) => Task.FromResult(_secrets.GetValueOrDefault(key));
        public Task DeleteAsync(string key) { _secrets.Remove(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task LegacyWorkspaceMigratesAndSurvivesReloadAndEdits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-migration-{Guid.NewGuid():N}.json");
        try
        {
            var connection = new ConnectionInfo { Name = "Local", ConnectionString = "Data Source=a.db" };
            const string legacyKey = "F552BE1A2BA8AAC8E77E76A92EB92B6B0E5050AADE925DEA5148C710C63C45EE";
            await File.WriteAllTextAsync(path, $$$$"""
                {"HistoryByConnection":{"{{{{legacyKey}}}}":[{"Sql":"SELECT 17"}]},
                 "ColumnLayouts":{"{{{{legacyKey}}}}\nCustomers":{"Order":["Name","Id"],"Hidden":["Id"],"FrozenCount":1}}}
                """);
            await new QueryWorkspaceService(path).MigrateConnectionsAsync([connection]);
            connection.Name = "Renamed";
            connection.ConnectionString = "Data Source=b.db";
            var workspace = new QueryWorkspaceService(path);
            Assert.Equal("SELECT 17", Assert.Single(await workspace.LoadHistoryAsync(connection)).Sql);
            var layout = await workspace.LoadTableColumnLayoutAsync(connection, "Customers");
            Assert.Equal(["Name", "Id"], layout.Order);
            Assert.Equal(["Id"], layout.Hidden);
            Assert.Equal(1, layout.FrozenCount);
            await workspace.MigrateConnectionsAsync([connection]);
            Assert.Single(await new QueryWorkspaceService(path).LoadHistoryAsync(connection));
            Assert.DoesNotContain(legacyKey, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EditsKeepHistoryAndColumnLayouts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-identity-{Guid.NewGuid():N}.json");
        try
        {
            var connection = new ConnectionInfo { Name = "Before", ConnectionString = "Host=old;Password=old" };
            var workspace = new QueryWorkspaceService(path);
            await workspace.AddHistoryEntryAsync(connection, new QueryHistoryEntry { Sql = "SELECT 42" });
            await workspace.SaveTableColumnLayoutAsync(connection, "Customers", new TableColumnLayout
            {
                Order = ["Name", "Id"],
                Hidden = ["Id"],
                FrozenCount = 1
            });
            connection.Name = "After";
            connection.ConnectionString = "Host=new;Password=new";
            var reloaded = new QueryWorkspaceService(path);
            Assert.Equal("SELECT 42", Assert.Single(await reloaded.LoadHistoryAsync(connection)).Sql);
            var layout = await reloaded.LoadTableColumnLayoutAsync(connection, "Customers");
            Assert.Equal(["Name", "Id"], layout.Order);
            Assert.Equal(["Id"], layout.Hidden);
            Assert.Equal(1, layout.FrozenCount);
        }
        finally { File.Delete(path); }
    }
}
