using DuckDB.NET.Data;

namespace DbConnect.Services;

internal static class DuckDbConnections
{
    // ── Connection helpers ────────────────────────────────────────────────────

    private static string NormalizeConnectionString(string connectionString)
    {
        var cs = connectionString.Trim();
        // If bare path or :memory:, wrap it
        if (!cs.Contains('='))
            return $"Data Source={cs}";
        return cs;
    }

    internal static DuckDBConnection Create(string connectionString) =>
        new DuckDBConnection(NormalizeConnectionString(connectionString));
}
