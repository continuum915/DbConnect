namespace DbConnect.Services;

/// <summary>Reads the database endpoint a tunnel forwards to, and points a connection string at the tunnel's local port.</summary>
public static class TunnelEndpoint
{
    /// <summary>
    /// Extracts the remote database host and port from a saved connection string.
    /// Used to tell the tunnel where to forward traffic.
    /// </summary>
    public static (string host, int port) ParseHostPort(string provider, string connectionString)
    {
        if (ConnectionStringFormats.For(provider) is not { UsesDatabaseFile: false } format)
            return ("localhost", 0);

        var fields = format.Parse(connectionString);
        return (string.IsNullOrEmpty(fields.Host) ? "localhost" : fields.Host, fields.Port ?? format.DefaultPort ?? 0);
    }

    /// <summary>
    /// Returns a copy of <paramref name="connectionString"/> with the host replaced by
    /// 127.0.0.1 and the port replaced by <paramref name="localPort"/> so the connection
    /// goes through the local tunnel endpoint.
    /// </summary>
    public static string PatchHostPort(string provider, string connectionString, int localPort) =>
        ConnectionStringFormats.For(provider)?.WithServer(connectionString, "127.0.0.1", localPort) ?? connectionString;
}
