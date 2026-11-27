using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Owns the file and independent catalog connections used by schema-change workflows.</summary>
internal sealed class SchemaChangeTestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rain-metadata-{Guid.NewGuid():N}.db");
    private readonly List<DatabaseSession> _sessions = [];

    public DatabaseSession OpenConnection()
    {
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={_path}" }, new SqliteProvider());
        _sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Catalog.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
