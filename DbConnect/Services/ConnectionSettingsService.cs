using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Persists connection metadata to plain JSON and stores passwords / SSH secrets
/// separately via <see cref="ICredentialStore"/>.
/// </summary>
public class ConnectionSettingsService
{
    private readonly string _metaFilePath;
    private readonly string _legacyFilePath;
    private readonly ICredentialStore _store;
    private readonly QueryWorkspaceService _workspace;

    public ConnectionSettingsService()
        : this(EncryptedFileStore.CreateForCurrentPlatform()) { }

    public ConnectionSettingsService(ICredentialStore store, string? directory = null)
    {
        _store = store;

        var dir = directory ?? AppDataPaths.RootDirectory;
        Directory.CreateDirectory(dir);

        _metaFilePath = Path.Combine(dir, "connections-meta.json");
        _legacyFilePath = Path.Combine(dir, "connections.json");
        _workspace = new QueryWorkspaceService(Path.Combine(dir, "query-workspace.json"));
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task<List<ConnectionInfo>> LoadConnectionsAsync()
    {
        // One-time migration from the old DPAPI-encrypted file
        await MigrateLegacyFileIfPresentAsync();

        if (!File.Exists(_metaFilePath))
            return new List<ConnectionInfo>();

        try
        {
            var json = await File.ReadAllTextAsync(_metaFilePath);
            var metas = JsonSerializer.Deserialize<List<ConnectionMetadata>>(json) ?? new();
            var results = new List<ConnectionInfo>();
            var needsIds = false;
            var hasEmbeddedPasswords = false;

            foreach (var m in metas)
            {
                needsIds |= m.Id == Guid.Empty;
                var secretKey = m.Id == Guid.Empty ? m.Name : m.Id.ToString();
                // Earlier versions saved MySQL passwords inside the metadata. Such a password is
                // what the connection used, so it wins; the rewrite below moves it to the store.
                var (storedConnStr, embeddedPassword) = StripPassword(m.Provider, m.ConnectionStringNoPassword);
                hasEmbeddedPasswords |= embeddedPassword.Length > 0;
                var password = embeddedPassword.Length > 0
                    ? embeddedPassword
                    : await _store.GetAsync(PasswordKey(secretKey)) ?? string.Empty;
                var connStr = InjectPassword(m.Provider, storedConnStr, password);

                var ssh = m.SshTunnel;
                if (ssh != null)
                {
                    ssh.SshPassword = await _store.GetAsync(SshPasswordKey(secretKey)) ?? string.Empty;
                    ssh.PrivateKeyPassphrase = await _store.GetAsync(SshKeyPhraseKey(secretKey)) ?? string.Empty;
                }

                results.Add(new ConnectionInfo
                {
                    Id = m.Id == Guid.Empty ? Guid.NewGuid() : m.Id,
                    Name = m.Name,
                    Provider = m.Provider,
                    ConnectionString = connStr,
                    SslMode = m.SslMode,
                    SslClientKeyPath = m.SslClientKeyPath,
                    SslClientCertPath = m.SslClientCertPath,
                    SslCaCertPath = m.SslCaCertPath,
                    TrustServerCertificate = m.TrustServerCertificate,
                    SshTunnel = ssh,
                    Group = m.Group,
                    LabelColor = m.LabelColor,
                    EnvironmentTier = string.IsNullOrEmpty(m.EnvironmentTier) ? "Development" : m.EnvironmentTier,
                    Options = m.Options ?? new ConnectionOptions(),
                    ShowAllDatabases = m.ShowAllDatabases
                });
            }

            if ((needsIds || hasEmbeddedPasswords) && await TryRewriteMetadataAsync(results) && needsIds)
            {
                foreach (var m in metas)
                    if (m.Id == Guid.Empty)
                        await DeleteConnectionSecretsAsync(m.Name);
            }
            await TryMigrateWorkspaceAsync(results);
            await SessionConnectionMigration.MigrateAsync(Path.GetDirectoryName(_metaFilePath)!, results);
            return results;
        }
        catch
        {
            return new List<ConnectionInfo>();
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public async Task SaveConnectionsAsync(IEnumerable<ConnectionInfo> connections)
    {
        var metas = new List<ConnectionMetadata>();

        foreach (var c in connections)
        {
            if (c.Id == Guid.Empty)
                c.Id = Guid.NewGuid();
            var (connStrNoPassword, password) = StripPassword(c.Provider, c.ConnectionString);

            metas.Add(new ConnectionMetadata
            {
                Id = c.Id,
                Name = c.Name,
                Provider = c.Provider,
                ConnectionStringNoPassword = connStrNoPassword,
                SslMode = c.SslMode,
                SslClientKeyPath = c.SslClientKeyPath,
                SslClientCertPath = c.SslClientCertPath,
                SslCaCertPath = c.SslCaCertPath,
                TrustServerCertificate = c.TrustServerCertificate,
                SshTunnel = c.SshTunnel != null ? new SshTunnelConfig
                {
                    Enabled = c.SshTunnel.Enabled,
                    SshHost = c.SshTunnel.SshHost,
                    SshPort = c.SshTunnel.SshPort,
                    SshUsername = c.SshTunnel.SshUsername,
                    AuthMethod = c.SshTunnel.AuthMethod,
                    PrivateKeyPath = c.SshTunnel.PrivateKeyPath,
                    LocalPort = c.SshTunnel.LocalPort
                    // Passwords stripped — stored in credential store below
                } : null,
                Group = c.Group,
                LabelColor = c.LabelColor,
                EnvironmentTier = c.EnvironmentTier,
                Options = c.Options,
                ShowAllDatabases = c.ShowAllDatabases
            });

            var secretKey = c.Id.ToString();
            await SaveSecretAsync(PasswordKey(secretKey), password);
            await SaveSecretAsync(SshPasswordKey(secretKey), c.SshTunnel?.SshPassword);
            await SaveSecretAsync(SshKeyPhraseKey(secretKey), c.SshTunnel?.PrivateKeyPassphrase);
        }

        var json = JsonSerializer.Serialize(metas, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_metaFilePath, json);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteConnectionAsync(ConnectionInfo connection)
    {
        await _workspace.DeleteConnectionAsync(connection);
        await DeleteConnectionSecretsAsync(connection.Id.ToString());
    }

    /// <summary>Removes credentials keyed by a connection ID or a legacy connection name.</summary>
    public async Task DeleteConnectionSecretsAsync(string connectionKey)
    {
        await _store.DeleteAsync(PasswordKey(connectionKey));
        await _store.DeleteAsync(SshPasswordKey(connectionKey));
        await _store.DeleteAsync(SshKeyPhraseKey(connectionKey));
    }

    /// <summary>
    /// Rewrites loaded connections in the current format. A failure leaves the existing file in
    /// place, so the connections still load and the rewrite is retried on the next load.
    /// </summary>
    private async Task<bool> TryRewriteMetadataAsync(IReadOnlyList<ConnectionInfo> connections)
    {
        try
        {
            await SaveConnectionsAsync(connections);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diagnostics = DiagnosticsService.Default;
            diagnostics.RecordFailure(diagnostics.BeginOperation("connections", "rewrite-metadata"), ex,
                "Saved connections could not be rewritten; the previous file was kept and the rewrite will be retried.");
            return false;
        }
    }

    private Task SaveSecretAsync(string key, string? value) => string.IsNullOrEmpty(value)
        ? _store.DeleteAsync(key)
        : _store.SetAsync(key, value);

    private async Task TryMigrateWorkspaceAsync(IReadOnlyList<ConnectionInfo> connections)
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(_metaFilePath)!, "query-workspace.json")))
            return;
        try
        {
            await _workspace.MigrateConnectionsAsync(connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Auxiliary state must not hide valid saved connections. Retry on the next load.
        }
    }

    // ── Legacy migration ──────────────────────────────────────────────────────

    private async Task MigrateLegacyFileIfPresentAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(_legacyFilePath)) return;
        if (File.Exists(_metaFilePath)) { File.Delete(_legacyFilePath); return; }

        try
        {
            var json = await File.ReadAllTextAsync(_legacyFilePath);
            var wrappers = JsonSerializer.Deserialize<List<LegacyEncryptedConnectionInfo>>(json);
            if (wrappers == null) return;

            var connections = new List<ConnectionInfo>();
            foreach (var w in wrappers)
            {
                var decryptedBytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(w.EncryptedConnectionString),
                    null, DataProtectionScope.CurrentUser);

                connections.Add(new ConnectionInfo
                {
                    Name = w.Name,
                    Provider = w.Provider,
                    ConnectionString = Encoding.UTF8.GetString(decryptedBytes),
                    TrustServerCertificate = true
                });
            }

            await SaveConnectionsAsync(connections);
            File.Delete(_legacyFilePath);
        }
        catch { /* Leave legacy file; will retry next launch */ }
    }

    // ── Password strip / inject ───────────────────────────────────────────────

    // Each provider's connection string format knows its own password key; file databases have none.

    private static (string connStrNoPassword, string password) StripPassword(
        string provider, string connectionString) =>
        ConnectionStringFormats.For(provider)?.WithoutPassword(connectionString) ?? (connectionString, string.Empty);

    private static string InjectPassword(string provider, string connectionString, string password) =>
        string.IsNullOrEmpty(password)
            ? connectionString
            : ConnectionStringFormats.For(provider)?.WithPassword(connectionString, password) ?? connectionString;

    // ── Credential key helpers ─────────────────────────────────────────────────

    private static string PasswordKey(string connectionKey) => $"rain.conn.{connectionKey}.password";
    private static string SshPasswordKey(string connectionKey) => $"rain.conn.{connectionKey}.sshpassword";
    private static string SshKeyPhraseKey(string connectionKey) => $"rain.conn.{connectionKey}.sshkeyphrase";
}

// ── Supporting types ──────────────────────────────────────────────────────────

internal class ConnectionMetadata
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ConnectionStringNoPassword { get; set; } = string.Empty;
    public string? SslMode { get; set; }
    public string? SslClientKeyPath { get; set; }
    public string? SslClientCertPath { get; set; }
    public string? SslCaCertPath { get; set; }
    // Missing values belong to saved records from before secure defaults.
    public bool TrustServerCertificate { get; set; } = true;
    public SshTunnelConfig? SshTunnel { get; set; }
    public string Group { get; set; } = string.Empty;
    public string? LabelColor { get; set; }
    public string? EnvironmentTier { get; set; }
    /// <summary>Additional properties never hold secrets; the dialog and the options dialect refuse such keys.</summary>
    public ConnectionOptions? Options { get; set; }
    public bool ShowAllDatabases { get; set; }
}

internal class LegacyEncryptedConnectionInfo
{
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string EncryptedConnectionString { get; set; } = string.Empty;
}

