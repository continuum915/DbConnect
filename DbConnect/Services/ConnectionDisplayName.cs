using System;

namespace DbConnect.Services;

/// <summary>
/// How a session is named in pickers and the status bar: "Name (Provider)", or "Name / database (Provider)" for a
/// server connection. The provider is added only when the connection's name doesn't already contain it.
/// </summary>
public static class ConnectionDisplayName
{
    public static string Format(string name, string? providerName, string? database = null)
    {
        var label = string.IsNullOrEmpty(database) ? name : $"{name} / {database}";
        return string.IsNullOrEmpty(providerName) || name.Contains(providerName, StringComparison.OrdinalIgnoreCase)
            ? label
            : $"{label} ({providerName})";
    }

    public static string Of(DatabaseSession session) =>
        Format(session.Name, session.ProviderName, session.Server != null ? session.DatabaseLabel : null);
}
