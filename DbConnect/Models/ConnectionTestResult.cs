using System;

namespace DbConnect.Models;

/// <summary>Why a connection test failed. Each provider maps its own driver exceptions onto these.</summary>
public enum ConnectionFailureCategory
{
    /// <summary>The test succeeded.</summary>
    None,
    Authentication,
    HostUnreachable,
    TlsCertificate,
    DatabaseNotFound,
    Timeout,
    SshAuthentication,
    SshHostUnreachable,
    SshHostKeyRejected,
    CertificateFileMissing,
    Other
}

/// <summary>
/// The outcome of testing a connection: on success the server reached and how long it took, on failure
/// a category, a one-line summary and the full driver detail.
/// </summary>
public sealed record ConnectionTestResult
{
    public bool Success => Category == ConnectionFailureCategory.None;
    public ConnectionFailureCategory Category { get; private init; }
    public string Summary { get; private init; } = string.Empty;
    public string Detail { get; private init; } = string.Empty;
    public string ServerVersion { get; private init; } = string.Empty;
    public TimeSpan Elapsed { get; private init; }

    /// <summary>Short label for <see cref="Category"/> (e.g. "Authentication failed").</summary>
    public string CategoryLabel => Describe(Category).Label;

    public static ConnectionTestResult Succeeded(string serverVersion, TimeSpan elapsed) => new()
    {
        Summary = $"Connected to {serverVersion} in {elapsed.TotalMilliseconds:0} ms.",
        ServerVersion = serverVersion,
        Elapsed = elapsed
    };

    public static ConnectionTestResult Failed(ConnectionFailureCategory category, string detail, TimeSpan elapsed, string? summary = null) => new()
    {
        Category = category == ConnectionFailureCategory.None ? ConnectionFailureCategory.Other : category,
        Summary = summary ?? Describe(category).Advice,
        Detail = detail,
        Elapsed = elapsed
    };

    private static (string Label, string Advice) Describe(ConnectionFailureCategory category) => category switch
    {
        ConnectionFailureCategory.None => ("Connected", string.Empty),
        ConnectionFailureCategory.Authentication => ("Authentication failed", "The server rejected the user name or password."),
        ConnectionFailureCategory.HostUnreachable => ("Host unreachable", "No server answered at that host and port. Check the address, port and firewall."),
        ConnectionFailureCategory.TlsCertificate => ("TLS/certificate error", "The TLS handshake failed. Check the SSL mode and certificate settings."),
        ConnectionFailureCategory.DatabaseNotFound => ("Database not found", "The database does not exist, or this user may not open it. Check the database name."),
        ConnectionFailureCategory.Timeout => ("Timed out", "The server did not respond in time. The network may be slow or the port filtered."),
        ConnectionFailureCategory.SshAuthentication => ("SSH authentication failed", "The SSH server rejected the user name, password or key."),
        ConnectionFailureCategory.SshHostUnreachable => ("SSH host unreachable", "The SSH host could not be reached. Check the SSH host and port."),
        ConnectionFailureCategory.SshHostKeyRejected => ("SSH host key rejected", "The SSH host key was not trusted, so the connection was not made."),
        ConnectionFailureCategory.CertificateFileMissing => ("Certificate file missing", "A configured certificate or key file is missing or unreadable."),
        _ => ("Connection failed", "The connection failed. See details for the driver message.")
    };
}
