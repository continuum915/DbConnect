using System;

namespace DbConnect.Models;

public class ConnectionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "SQLite";
    public string ConnectionString { get; set; } = string.Empty;

    // SSL / TLS — network providers only; nullable so old saved records deserialise cleanly
    public string? SslMode { get; set; }
    public string? SslClientKeyPath { get; set; }
    public string? SslClientCertPath { get; set; }
    public string? SslCaCertPath { get; set; }
    public bool TrustServerCertificate { get; set; }

    // SSH tunnel — null means no tunnel configured
    public SshTunnelConfig? SshTunnel { get; set; }

    // Organisation / display
    /// <summary>Group label for the connection list (empty = ungrouped).</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Hex colour string for the label dot (e.g. "#4CAF50"). Null = no colour.</summary>
    public string? LabelColor { get; set; }

    /// <summary>Environment tier: "Development", "Staging", or "Production".</summary>
    public string EnvironmentTier { get; set; } = "Development";

    /// <summary>
    /// Lists every database on the server under the connection. False pins the connection to its one database,
    /// as connections saved before this option behave. New server connections default to true.
    /// </summary>
    public bool ShowAllDatabases { get; set; }

    /// <summary>Authentication, timeouts, read-only, session setup and extra driver properties.</summary>
    public ConnectionOptions Options { get; set; } = new();

    /// <summary>A shallow copy; the SSH tunnel settings and options are shared, not copied.</summary>
    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();
}
