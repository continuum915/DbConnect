using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Turns a saved connection into a usable one. The connection dialog's Test and the main window's connect
/// both go through here, so neither keeps its own connection logic. The pipeline is
/// resolve secrets → tunnel → rewrite endpoint → apply options and TLS → test or establish. It runs off the calling thread,
/// and on failure or cancellation everything the establisher opened is disposed before control returns.
/// Metadata loads use the session catalog cancellation. Legacy connection-test calls are abandoned
/// on cancellation and close their own connections when the driver returns.
/// </summary>
public sealed class ConnectionEstablisher
{
    /// <summary>How often an open tunnel sends an SSH keep-alive, so idle tunnels are not dropped.</summary>
    public static readonly TimeSpan TunnelKeepAlive = TimeSpan.FromSeconds(30);

    private readonly DatabaseService _databases;
    private readonly ISshTransport _ssh;
    private readonly SshHostTrust _hostTrust;

    public ConnectionEstablisher(DatabaseService databases, ISshTransport? ssh = null, KnownHostsStore? knownHosts = null)
    {
        _databases = databases;
        _ssh = ssh ?? new SshNetTransport();
        _hostTrust = new SshHostTrust(knownHosts ?? new KnownHostsStore());
    }

    /// <summary>Tests the connection and always tears down anything opened to do so.</summary>
    public Task<ConnectionTestResult> TestAsync(ConnectionInfo connection, CancellationToken cancellationToken = default, SshTrustPrompt? trustPrompt = null) =>
        Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var owned = new List<IDisposable>();
            try
            {
                ConnectionInfo settings;
                try
                {
                    settings = await PrepareAsync(connection, owned, cancellationToken, trustPrompt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ConnectionTester.Failed(ex, stopwatch.Elapsed);
                }
                var provider = _databases.GetProvider(settings.Provider);
                return await provider.TestConnectionAsync(settings.ConnectionString).WaitAsync(cancellationToken);
            }
            finally
            {
                Release(owned);
            }
        }, cancellationToken);

    /// <summary>
    /// Opens a session on the effective settings and loads its schema. The caller owns the result and disposes it
    /// on disconnect; if any step fails or is cancelled, nothing is handed back and nothing is left open.
    /// </summary>
    public Task<EstablishedConnection> EstablishAsync(ConnectionInfo connection, CancellationToken cancellationToken = default, SshTrustPrompt? trustPrompt = null) =>
        Task.Run(async () =>
        {
            var owned = new List<IDisposable>();
            try
            {
                var settings = await PrepareAsync(connection, owned, cancellationToken, trustPrompt);
                var session = _databases.CreateSession(settings);
                owned.Add(session.Catalog);
                var schema = await session.GetSchemaAsync().WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new EstablishedConnection(session, schema, owned);
            }
            catch
            {
                Release(owned);
                throw;
            }
        }, cancellationToken);

    /// <summary>Builds the effective settings. Anything a step opens goes into <paramref name="owned"/>.</summary>
    private async Task<ConnectionInfo> PrepareAsync(ConnectionInfo saved, List<IDisposable> owned, CancellationToken cancellationToken, SshTrustPrompt? trustPrompt)
    {
        var settings = ResolveSecrets(saved);
        cancellationToken.ThrowIfCancellationRequested();
        settings = await OpenTunnelAsync(settings, owned, cancellationToken, trustPrompt);
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyTls(ApplyOptions(settings));
    }

    /// <summary>Puts the connection options' driver keywords (auth, timeouts, read-only, extra properties) in the string.</summary>
    private static ConnectionInfo ApplyOptions(ConnectionInfo settings)
    {
        if (ConnectionOptionDialects.For(settings.Provider) is { } dialect)
            settings.ConnectionString = dialect.Apply(settings.ConnectionString, settings.Options ?? new ConnectionOptions());
        return settings;
    }

    /// <summary>
    /// The effective settings start as a copy of the saved connection, so later steps can rewrite them without
    /// touching what was saved. Secrets already arrive injected by <see cref="ConnectionSettingsService"/>,
    /// so this step only copies until a vault lookup moves here.
    /// </summary>
    private static ConnectionInfo ResolveSecrets(ConnectionInfo saved) => saved.Clone();

    /// <summary>
    /// Opens the SSH tunnel when one is configured and rewrites the endpoint to its local port.
    /// Unknown and changed host keys require an explicit interactive trust decision.
    /// </summary>
    private async Task<ConnectionInfo> OpenTunnelAsync(ConnectionInfo settings, List<IDisposable> owned, CancellationToken cancellationToken, SshTrustPrompt? trustPrompt)
    {
        if (settings.SshTunnel?.Enabled != true)
            return settings;

        var (remoteHost, remotePort) = TunnelEndpoint.ParseHostPort(settings.Provider, settings.ConnectionString);
        var request = new SshTunnelRequest(settings.SshTunnel, remoteHost, remotePort, TunnelKeepAlive, AcceptHostKey: key => _hostTrust.Accept(key, trustPrompt, cancellationToken));
        var tunnel = await _ssh.OpenAsync(request, cancellationToken);
        owned.Add(tunnel);
        cancellationToken.ThrowIfCancellationRequested();
        settings.ConnectionString = TunnelEndpoint.PatchHostPort(settings.Provider, settings.ConnectionString, tunnel.LocalPort);
        return settings;
    }

    /// <summary>Checks configured TLS files before the provider opens a connection.</summary>
    private static ConnectionInfo ApplyTls(ConnectionInfo settings)
    {
        var format = ConnectionStringFormats.For(settings.Provider);
        var fields = format is { SupportsCaCertificate: true } or { SupportsClientCertificate: true } or { SupportsClientKey: true }
            ? format.Parse(settings.ConnectionString) : null;
        foreach (var path in new[]
        {
            settings.SslCaCertPath, settings.SslClientCertPath, settings.SslClientKeyPath,
            fields?.SslCaCertPath, fields?.SslClientCertPath, fields?.SslClientKeyPath
        })
        {
            if (string.IsNullOrEmpty(path))
                continue;
            try
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new CertificateFileException(path, ex);
            }
        }
        return settings;
    }

    private static void Release(List<IDisposable> owned)
    {
        for (var i = owned.Count - 1; i >= 0; i--)
            owned[i].Dispose();
        owned.Clear();
    }
}
