using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Connection passwords live in the credential store, never in connections-meta.json.</summary>
public sealed class ConnectionPasswordStorageTests : IDisposable
{
    private const string Secret = "S3cret-pw;with=chars";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-passwords-{Guid.NewGuid():N}");

    public ConnectionPasswordStorageTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private string MetadataPath => Path.Combine(_directory, "connections-meta.json");

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public async Task SavedPasswordIsKeptOutOfMetadataAndRestoredOnLoad(string provider)
    {
        var store = new MemoryCredentialStore();
        var settings = new ConnectionSettingsService(store, _directory);

        await settings.SaveConnectionsAsync([Connection(provider)]);

        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(MetadataPath));
        Assert.Contains(Secret, store.Values);
        var loaded = Assert.Single(await settings.LoadConnectionsAsync());
        Assert.Equal(Secret, ConnectionStringFormats.For(provider)!.Parse(loaded.ConnectionString).Password);
    }

    [Fact]
    public async Task PlaintextMySqlPasswordFromEarlierVersionsMovesToCredentialStore()
    {
        var connection = Connection("MySQL");
        await WriteMetadataAsync(connection.Id, connection.ConnectionString);
        var store = new MemoryCredentialStore();
        var settings = new ConnectionSettingsService(store, _directory);

        var loaded = Assert.Single(await settings.LoadConnectionsAsync());

        Assert.Equal(Secret, ConnectionStringFormats.For("MySQL")!.Parse(loaded.ConnectionString).Password);
        Assert.Equal(connection.Id, loaded.Id);
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(MetadataPath));
        Assert.Contains(Secret, store.Values);

        var reloaded = Assert.Single(await new ConnectionSettingsService(store, _directory).LoadConnectionsAsync());
        Assert.Equal(Secret, ConnectionStringFormats.For("MySQL")!.Parse(reloaded.ConnectionString).Password);
    }

    [Fact]
    public async Task FailedMigrationStillLoadsConnectionAndLeavesFileForNextAttempt()
    {
        var connection = Connection("MySQL");
        await WriteMetadataAsync(connection.Id, connection.ConnectionString);
        var before = await File.ReadAllTextAsync(MetadataPath);
        var settings = new ConnectionSettingsService(new MemoryCredentialStore { FailWrites = true }, _directory);

        var loaded = Assert.Single(await settings.LoadConnectionsAsync());

        Assert.Equal(Secret, ConnectionStringFormats.For("MySQL")!.Parse(loaded.ConnectionString).Password);
        Assert.Equal(before, await File.ReadAllTextAsync(MetadataPath));
    }

    private static ConnectionInfo Connection(string provider) => new()
    {
        Name = $"{provider} connection",
        Provider = provider,
        ConnectionString = ConnectionStringFormats.For(provider)!.Build(new ConnectionFields
        {
            Host = "db.example",
            Database = "app",
            Username = "app_user",
            Password = Secret
        })
    };

    /// <summary>The shape earlier versions wrote for MySQL: the full connection string, password included.</summary>
    private Task WriteMetadataAsync(Guid id, string connectionString) =>
        File.WriteAllTextAsync(MetadataPath, JsonSerializer.Serialize(new[]
        {
            new { Id = id, Name = "Legacy MySQL", Provider = "MySQL", ConnectionStringNoPassword = connectionString }
        }));

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public bool FailWrites { get; init; }
        public IEnumerable<string> Values => _secrets.Values;

        public Task SetAsync(string key, string secret)
        {
            if (FailWrites) throw new InvalidOperationException("Credential store unavailable.");
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult(_secrets.GetValueOrDefault(key));

        public Task DeleteAsync(string key)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}
