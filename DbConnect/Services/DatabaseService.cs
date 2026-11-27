using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

/// <summary>
/// RAIN-105 — Central provider registry. Register all providers here at
/// startup; everything else resolves providers through this service.
/// </summary>
public class DatabaseService
{
    private readonly Dictionary<string, IDatabaseProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DiagnosticsService _diagnostics;

    public DatabaseService(DiagnosticsService? diagnostics = null)
    {
        _diagnostics = diagnostics ?? DiagnosticsService.Default;
        RegisterProvider(new SqliteProvider());
        RegisterProvider(new PostgreSqlProvider());
        RegisterProvider(new SqlServerProvider());
        RegisterProvider(new MySqlProvider());
        RegisterProvider(new DuckDbProvider());
    }

    public void RegisterProvider(IDatabaseProvider provider) =>
        _providers[provider.ProviderName] = provider;

    public IEnumerable<string> GetAvailableProviders() => _providers.Keys;

    public IDatabaseProvider GetProvider(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
            return provider;
        throw new NotSupportedException($"Provider '{name}' is not supported.");
    }

    public DatabaseSession CreateSession(Models.ConnectionInfo connection) =>
        new(connection, GetProvider(connection.Provider), _diagnostics);

    /// <summary>Returns the icon badge for a given provider name.</summary>
    public string GetProviderIcon(string name) =>
        _providers.TryGetValue(name, out var provider) ? provider.ProviderIcon : "🔌";
}
