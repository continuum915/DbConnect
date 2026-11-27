using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// The other databases reachable from a session on a server connection that shows all databases:
/// their names, the SQL that reads the session's current database after USE, and a way to open one.
/// </summary>
public sealed class ServerDatabaseDirectory(Func<IReadOnlyList<string>> list, Func<string, Task<DatabaseSession?>> open,
    string? currentDatabaseSql)
{
    public IReadOnlyList<string> Databases => list();
    public string? CurrentDatabaseSql { get; } = currentDatabaseSql;
    public Task<DatabaseSession?> OpenAsync(string database) => open(database);
}
