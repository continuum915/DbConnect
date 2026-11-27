using System;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Opens SSH tunnels for the <see cref="ConnectionEstablisher"/>. The real transport is <see cref="SshNetTransport"/>;
/// tests supply a fake that records opens and closes.
/// </summary>
public interface ISshTransport
{
    /// <summary>
    /// Connects to the SSH host and forwards a local port to the remote endpoint. Failures throw
    /// <see cref="SshTunnelException"/>. On failure or cancellation nothing is left open.
    /// </summary>
    Task<ISshTunnel> OpenAsync(SshTunnelRequest request, CancellationToken cancellationToken);
}

/// <summary>An open tunnel. Disposing closes the local port and the SSH session.</summary>
public interface ISshTunnel : IDisposable
{
    /// <summary>The loopback port that forwards to the remote endpoint.</summary>
    int LocalPort { get; }
}

/// <summary>
/// What to tunnel: the SSH settings, the database endpoint behind the bastion, the keep-alive interval, and the
/// host-key hook that decides whether the SSH host's key is trusted.
/// </summary>
public sealed record SshTunnelRequest(
    SshTunnelConfig Config,
    string RemoteHost,
    int RemotePort,
    TimeSpan KeepAliveInterval,
    Func<SshHostKey, bool> AcceptHostKey);

/// <summary>The key an SSH host presented: algorithm name and SHA-256 fingerprint.</summary>
public sealed record SshHostKey(string Host, int Port, string Algorithm, string Sha256Fingerprint);

/// <summary>An SSH step failed. <see cref="Category"/> is one of the SSH failure categories.</summary>
public sealed class SshTunnelException : Exception
{
    public SshTunnelException(ConnectionFailureCategory category, string message, Exception? innerException = null)
        : base(message, innerException) => Category = category;

    public ConnectionFailureCategory Category { get; }
}
