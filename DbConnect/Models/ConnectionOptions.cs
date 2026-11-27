using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Models;

/// <summary>How a connection signs in. Which modes a provider offers comes from its connection-string format.</summary>
public enum ConnectionAuthentication
{
    Password,
    /// <summary>Windows integrated security (SQL Server) or GSS/SSPI negotiated by the driver (PostgreSQL).</summary>
    Integrated,
    EntraInteractive,
    EntraIntegrated,
    EntraPassword,
    EntraServicePrincipal,
    EntraManagedIdentity,
    EntraDefault
}

/// <summary>An extra driver keyword the connection form does not expose.</summary>
public sealed class ConnectionProperty
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Per-connection options beyond host and credentials. Older saved connections have none, which keeps
/// today's behavior: password sign-in, driver default timeouts, writable, no session setup.
/// </summary>
public sealed class ConnectionOptions
{
    public ConnectionAuthentication Authentication { get; set; } = ConnectionAuthentication.Password;
    public int? ConnectTimeoutSeconds { get; set; }
    /// <summary>Default for new query tabs on this connection; a tab's own setting wins.</summary>
    public int? CommandTimeoutSeconds { get; set; }
    public int? KeepAliveSeconds { get; set; }
    /// <summary>DbConnect refuses SQL that may change data, and the server enforces read-only where it can.</summary>
    public bool IsReadOnly { get; set; }
    public string? DefaultSchema { get; set; }
    /// <summary>Runs on every new query or table session, before the session is used.</summary>
    public string? StartupSql { get; set; }
    public List<ConnectionProperty> AdditionalProperties { get; set; } = [];

    public ConnectionOptions Copy() => new()
    {
        Authentication = Authentication,
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        KeepAliveSeconds = KeepAliveSeconds,
        IsReadOnly = IsReadOnly,
        DefaultSchema = DefaultSchema,
        StartupSql = StartupSql,
        AdditionalProperties = AdditionalProperties.Select(p => new ConnectionProperty { Key = p.Key, Value = p.Value }).ToList()
    };
}
