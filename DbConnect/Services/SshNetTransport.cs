using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DbConnect.Services;

/// <summary>
/// The SSH.NET transport: connects with the configured password or private key, asks the request's host-key hook
/// whether to trust the server, sets the keep-alive interval and forwards a loopback port to the remote endpoint.
/// </summary>
public sealed class SshNetTransport : ISshTransport
{
    public async Task<ISshTunnel> OpenAsync(SshTunnelRequest request, CancellationToken cancellationToken)
    {
        var cfg = request.Config;
        var hostKeyRejected = false;
        Exception? hostKeyError = null;
        SshClient? client = null;
        try
        {
            client = new SshClient(new Renci.SshNet.ConnectionInfo(cfg.SshHost, cfg.SshPort, cfg.SshUsername, Authentication(cfg)));
            client.KeepAliveInterval = request.KeepAliveInterval;
            client.HostKeyReceived += (_, e) =>
            {
                try
                {
                    e.CanTrust = request.AcceptHostKey(new SshHostKey(cfg.SshHost, cfg.SshPort, e.HostKeyName, e.FingerPrintSHA256));
                }
                catch (Exception ex)
                {
                    hostKeyError = ex;
                    e.CanTrust = false;
                }
                hostKeyRejected = !e.CanTrust;
            };
            await client.ConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var port = new ForwardedPortLocal("127.0.0.1", (uint)cfg.LocalPort, request.RemoteHost, (uint)request.RemotePort);
            client.AddForwardedPort(port);
            try
            {
                port.Start();
            }
            catch (Exception ex)
            {
                throw new SshTunnelException(ConnectionFailureCategory.Other,
                    $"Could not open local port {cfg.LocalPort} for the SSH tunnel: {ex.Message}", ex);
            }
            return new Tunnel(client, port);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SshTunnelException)
        {
            client?.Dispose();
            if (hostKeyError is OperationCanceledException cancelled)
                throw new OperationCanceledException(cancelled.Message, cancelled, cancellationToken);
            var category = hostKeyRejected ? ConnectionFailureCategory.SshHostKeyRejected : Classify(ex);
            throw new SshTunnelException(category, $"SSH tunnel to {cfg.SshHost}:{cfg.SshPort} failed: {hostKeyError?.Message ?? ex.Message}", hostKeyError ?? ex);
        }
        catch
        {
            client?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Maps an SSH.NET failure to an SSH category: credential and key-file problems are SSH authentication,
    /// anything that stops the SSH session being reached or negotiated is SSH host unreachable.
    /// </summary>
    public static ConnectionFailureCategory Classify(Exception exception) => exception switch
    {
        SshAuthenticationException or SshPassPhraseNullOrEmptyException => ConnectionFailureCategory.SshAuthentication,
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => ConnectionFailureCategory.SshAuthentication,
        SocketException or SshOperationTimeoutException or ProxyException or SshConnectionException => ConnectionFailureCategory.SshHostUnreachable,
        // Thrown by PrivateKeyFile for an unreadable key or a wrong passphrase.
        SshException => ConnectionFailureCategory.SshAuthentication,
        _ => ConnectionFailureCategory.SshHostUnreachable
    };

    private static AuthenticationMethod Authentication(SshTunnelConfig cfg)
    {
        if (cfg.AuthMethod != "PrivateKey")
            return new PasswordAuthenticationMethod(cfg.SshUsername, cfg.SshPassword);
        var key = string.IsNullOrWhiteSpace(cfg.PrivateKeyPassphrase)
            ? new PrivateKeyFile(cfg.PrivateKeyPath)
            : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase);
        return new PrivateKeyAuthenticationMethod(cfg.SshUsername, key);
    }

    private sealed class Tunnel(SshClient client, ForwardedPortLocal port) : ISshTunnel
    {
        private bool _disposed;

        public int LocalPort { get; } = (int)port.BoundPort;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                port.Stop();
                client.Disconnect();
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
