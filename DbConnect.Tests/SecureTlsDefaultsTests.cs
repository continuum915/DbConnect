using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class SecureTlsDefaultsTests
{
    [Theory]
    [InlineData("SQL Server", "Required")]
    [InlineData("PostgreSQL", "VerifyFull")]
    public async Task NewConnectionsVerifyCertificates(string provider, string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-tls-{Guid.NewGuid():N}");
        try
        {
            var editor = new ConnectionDialogViewModel(new DatabaseService(), new ConnectionSettingsService(new MemoryStore(), directory))
            { SelectedProvider = provider, Host = "db" };
            await editor.Initialization;
            editor.SetResult();
            Assert.False(editor.TrustServerCertificate);
            Assert.False(editor.Result!.TrustServerCertificate);
            var fields = ConnectionStringFormats.For(provider)!.Parse(editor.Result.ConnectionString);
            Assert.Equal(mode, fields.SslMode);
            Assert.False(new ConnectionInfo().TrustServerCertificate);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("SQL Server", null, true)]
    [InlineData("SQL Server", true, true)]
    [InlineData("SQL Server", false, false)]
    [InlineData("PostgreSQL", null, true)]
    [InlineData("PostgreSQL", true, true)]
    [InlineData("PostgreSQL", false, false)]
    public async Task SavedTrustValuesSurviveLoadEditAndSave(string provider, bool? stored, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-tls-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var record = new Dictionary<string, object>
            {
                ["Name"] = "Saved",
                ["Provider"] = provider,
                ["ConnectionStringNoPassword"] = provider == "SQL Server" ? "Server=db;Encrypt=True" : "Host=db;SSL Mode=Require"
            };
            if (stored.HasValue) record["TrustServerCertificate"] = stored.Value;
            await File.WriteAllTextAsync(Path.Combine(directory, "connections-meta.json"), System.Text.Json.JsonSerializer.Serialize(new[] { record }));
            var settings = new ConnectionSettingsService(new MemoryStore(), directory);
            var connection = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.Equal(expected, connection.TrustServerCertificate);
            var editor = new ConnectionDialogViewModel(new DatabaseService(), settings) { SelectedSavedConnection = connection };
            await editor.Initialization;
            Assert.Equal(expected, editor.TrustServerCertificate);
            editor.SetResult();
            await settings.SaveConnectionsAsync([editor.Result!]);
            Assert.Equal(expected, Assert.Single(await settings.LoadConnectionsAsync()).TrustServerCertificate);
            Assert.Equal(provider == "SQL Server" ? "Required" : "Require", ConnectionStringFormats.For(provider)!.Parse(editor.Result!.ConnectionString).SslMode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("SQL Server", "Required")]
    [InlineData("PostgreSQL", "Require")]
    public async Task ExplicitDevelopmentOptOutPersists(string provider, string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-tls-optout-{Guid.NewGuid():N}");
        try
        {
            var settings = new ConnectionSettingsService(new MemoryStore(), directory);
            var editor = new ConnectionDialogViewModel(new DatabaseService(), settings)
            {
                SelectedProvider = provider,
                Host = "db",
                ConnectionName = "Development",
                TrustServerCertificate = true,
                SslMode = mode,
                SqlServerSslMode = mode
            };
            await editor.SaveConnectionCommand.ExecuteAsync(null);
            var loaded = Assert.Single(await settings.LoadConnectionsAsync());
            Assert.True(loaded.TrustServerCertificate);
            var reopened = new ConnectionDialogViewModel(new DatabaseService(), settings) { SelectedSavedConnection = loaded };
            await reopened.Initialization;
            reopened.SetResult();
            Assert.True(reopened.Result!.TrustServerCertificate);
            var fields = ConnectionStringFormats.For(provider)!.Parse(reopened.Result.ConnectionString);
            Assert.Equal(mode, fields.SslMode);
            if (provider == "SQL Server") Assert.True(fields.TrustServerCertificate);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MemoryStore : ICredentialStore
    {
        public Task SetAsync(string key, string secret) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
