using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Registry contract combining focused provider roles. Consumers that need only one
/// role can depend on that interface while registry and session callers keep this aggregate.
/// </summary>
public interface IDatabaseProvider : IDatabaseQueryProvider, IDatabaseSchemaProvider,
    IDatabaseRoutineProvider, IDatabaseAdministrationProvider
{
    /// <summary>Emoji badge shown next to connections in the sidebar.</summary>
    string ProviderIcon { get; }

    /// <summary>Structured provider behavior flags used by UI and SQL helpers.</summary>
    DatabaseProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Opens and immediately closes a connection, reporting the server version and elapsed time on success,
    /// or the failure category, summary and driver detail on failure. Never throws for connection failures.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(string connectionString);

    /// <summary>Returns the full server/library version string for display in the status bar.</summary>
    Task<string> GetServerVersionAsync(string connectionString);

    /// <summary>
    /// Opens a session: one connection owned by one tab, keeping transaction and
    /// session-scoped state between runs. The default is a stateless session that
    /// opens a connection per call; providers with real sessions override it.
    /// </summary>
    Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default) =>
        Task.FromResult<IDbSession>(new StatelessDbSession(this, connection));

    // ── Active database label ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the active database (or file) name for display in the status bar.
    /// Default: null (shown as connection name only).
    /// </summary>
    string? GetActiveDatabaseLabel(string connectionString) => null;

    /// <summary>
    /// Persists a routine definition using the provider's regular non-query execution path.
    /// </summary>
    async Task IDatabaseRoutineProvider.SaveProcedureAsync(string connectionString, string definition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await ExecuteNonQueryAsync(connectionString, definition);
    }
}
