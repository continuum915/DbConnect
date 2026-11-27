using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbConnect.Services;

/// <summary>One database on a server, as the explorer lists it.</summary>
public sealed record ServerDatabase(string Name, bool IsSystem, bool IsAccessible, string? Note = null);

/// <summary>
/// Lists the databases on a server and derives a connection string for each one. Servers whose provider
/// has none (SQLite, DuckDB) keep a connection to a single database.
/// </summary>
public interface IServerDatabaseCatalog
{
    Task<IReadOnlyList<ServerDatabase>> ListAsync(string connectionString, CancellationToken cancellationToken = default);

    /// <summary>The same connection string pointed at <paramref name="database"/>, every other setting kept.</summary>
    string WithDatabase(string connectionString, string database);

    /// <summary>The database a connection string names, or null for the server default.</summary>
    string? DatabaseOf(string connectionString);

    /// <summary>SQL returning the session's current database after USE; null when USE cannot change it.</summary>
    string? CurrentDatabaseSql { get; }
}

public static class ServerDatabaseCatalogs
{
    public static IServerDatabaseCatalog? For(string providerName) => providerName switch
    {
        "PostgreSQL" => new PostgreSqlCatalog(),
        "SQL Server" => new SqlServerCatalog(),
        "MySQL" => new MySqlCatalog(),
        _ => null
    };

    private static async Task<IReadOnlyList<ServerDatabase>> ReadAsync(System.Data.Common.DbConnection connection, string sql,
        Func<System.Data.Common.DbDataReader, ServerDatabase> map, CancellationToken ct)
    {
        await using (connection)
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var databases = new List<ServerDatabase>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) databases.Add(map(reader));
            return databases;
        }
    }

    private sealed class PostgreSqlCatalog : IServerDatabaseCatalog
    {
        public string? CurrentDatabaseSql => null;

        public Task<IReadOnlyList<ServerDatabase>> ListAsync(string connectionString, CancellationToken ct = default) =>
            ReadAsync(PostgreSqlConnections.Create(connectionString), """
                SELECT datname, datistemplate, datallowconn AND has_database_privilege(datname, 'CONNECT'),
                       CASE WHEN NOT datallowconn THEN 'does not allow connections'
                            WHEN NOT has_database_privilege(datname, 'CONNECT') THEN 'no CONNECT privilege' END
                FROM pg_database ORDER BY datname
                """,
                r => new(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3)), ct);

        public string WithDatabase(string connectionString, string database) =>
            new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

        public string? DatabaseOf(string connectionString) =>
            new NpgsqlConnectionStringBuilder(connectionString).Database is { Length: > 0 } name ? name : null;
    }

    private sealed class SqlServerCatalog : IServerDatabaseCatalog
    {
        public string? CurrentDatabaseSql => "SELECT DB_NAME()";

        public Task<IReadOnlyList<ServerDatabase>> ListAsync(string connectionString, CancellationToken ct = default) =>
            ReadAsync(new SqlConnection(connectionString), """
                SELECT name, CAST(CASE WHEN database_id <= 4 THEN 1 ELSE 0 END AS bit),
                       CAST(CASE WHEN state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 THEN 1 ELSE 0 END AS bit),
                       CASE WHEN state_desc <> 'ONLINE' THEN LOWER(state_desc)
                            WHEN HAS_DBACCESS(name) = 0 THEN 'no access'
                            WHEN user_access_desc <> 'MULTI_USER' THEN LOWER(user_access_desc) END
                FROM sys.databases ORDER BY name
                """,
                r => new(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3)), ct);

        public string WithDatabase(string connectionString, string database) =>
            new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

        public string? DatabaseOf(string connectionString) =>
            new SqlConnectionStringBuilder(connectionString).InitialCatalog is { Length: > 0 } name ? name : null;
    }

    private sealed class MySqlCatalog : IServerDatabaseCatalog
    {
        private static readonly HashSet<string> System = new(StringComparer.OrdinalIgnoreCase)
            { "mysql", "sys", "performance_schema", "information_schema" };

        public string? CurrentDatabaseSql => "SELECT DATABASE()";

        // SCHEMATA lists only the databases the user has some privilege on.
        public Task<IReadOnlyList<ServerDatabase>> ListAsync(string connectionString, CancellationToken ct = default) =>
            ReadAsync(MySqlConnections.Create(connectionString),
                "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME",
                r => new(r.GetString(0), System.Contains(r.GetString(0)), true), ct);

        public string WithDatabase(string connectionString, string database) =>
            new MySqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

        public string? DatabaseOf(string connectionString) =>
            new MySqlConnectionStringBuilder(connectionString).Database is { Length: > 0 } name ? name : null;
    }
}
