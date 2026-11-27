using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Services;

/// <summary>
/// Queries live server connections for active queries and provides kill-statement generation.
/// Embedded providers (SQLite, DuckDB) return empty lists.
/// </summary>
public class ConnectionMonitorService
{
    public async Task<IReadOnlyList<ActiveQueryInfo>> GetActiveQueriesAsync(DatabaseSession session)
    {
        var provider = session.ProviderName;

        if (IsEmbedded(provider))
            return Array.Empty<ActiveQueryInfo>();

        try
        {
            string sql = BuildActiveQueriesSql(provider);
            if (string.IsNullOrEmpty(sql))
                return Array.Empty<ActiveQueryInfo>();

            var result = await session.ExecuteQueryAsync(sql);
            if (result.HasError || !result.HasRows)
                return Array.Empty<ActiveQueryInfo>();

            var list = new List<ActiveQueryInfo>();
            foreach (var row in result.ObjectRows)
            {
                var info = MapRow(result, row, provider);
                if (info != null)
                    list.Add(info);
            }
            return list;
        }
        catch
        {
            return Array.Empty<ActiveQueryInfo>();
        }
    }

    public Task<string> GetKillStatementAsync(DatabaseSession session, string connectionId)
    {
        var provider = session.ProviderName;
        string stmt = provider switch
        {
            "PostgreSQL" => $"SELECT pg_terminate_backend({connectionId});",
            "SQL Server" => $"KILL {connectionId};",
            "MySQL" => $"KILL {connectionId};",
            _ => $"-- Kill not supported for {provider}"
        };
        return Task.FromResult(stmt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsEmbedded(string providerName) =>
        providerName is "SQLite" or "DuckDB";

    private static string BuildActiveQueriesSql(string provider) => provider switch
    {
        "PostgreSQL" =>
            "SELECT pid::text AS pid, usename, datname, state, " +
            "EXTRACT(EPOCH FROM (now() - query_start)) AS duration_sec, query " +
            "FROM pg_stat_activity WHERE state != 'idle' AND query_start IS NOT NULL " +
            "ORDER BY duration_sec DESC",

        "SQL Server" =>
            "SELECT CAST(r.session_id AS NVARCHAR) AS session_id, s.login_name, " +
            "DB_NAME(r.database_id) AS db_name, r.status, " +
            "r.total_elapsed_time / 1000.0 AS duration_sec, t.text AS query " +
            "FROM sys.dm_exec_requests r " +
            "JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id " +
            "CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t " +
            "WHERE r.session_id != @@SPID",

        "MySQL" =>
            "SELECT CAST(ID AS CHAR) AS id, USER, DB, STATE, TIME AS duration_sec, INFO AS query " +
            "FROM INFORMATION_SCHEMA.PROCESSLIST WHERE COMMAND != 'Sleep'",

        _ => string.Empty
    };

    private static ActiveQueryInfo? MapRow(QueryResult result, object?[] row, string provider)
    {
        try
        {
            var info = new ActiveQueryInfo();
            switch (provider)
            {
                case "PostgreSQL":
                    info.ConnectionId = result.ValueOrDefault(row, "pid")?.ToString() ?? string.Empty;
                    info.UserName = result.ValueOrDefault(row, "usename")?.ToString() ?? string.Empty;
                    info.Database = result.ValueOrDefault(row, "datname")?.ToString() ?? string.Empty;
                    info.State = result.ValueOrDefault(row, "state")?.ToString() ?? string.Empty;
                    info.QueryText = result.ValueOrDefault(row, "query")?.ToString() ?? string.Empty;
                    if (double.TryParse(result.ValueOrDefault(row, "duration_sec")?.ToString(), out var pgSec))
                        info.Duration = TimeSpan.FromSeconds(pgSec);
                    break;

                case "SQL Server":
                    info.ConnectionId = result.ValueOrDefault(row, "session_id")?.ToString() ?? string.Empty;
                    info.UserName = result.ValueOrDefault(row, "login_name")?.ToString() ?? string.Empty;
                    info.Database = result.ValueOrDefault(row, "db_name")?.ToString() ?? string.Empty;
                    info.State = result.ValueOrDefault(row, "status")?.ToString() ?? string.Empty;
                    info.QueryText = result.ValueOrDefault(row, "query")?.ToString() ?? string.Empty;
                    if (double.TryParse(result.ValueOrDefault(row, "duration_sec")?.ToString(), out var ssSec))
                        info.Duration = TimeSpan.FromSeconds(ssSec);
                    break;

                case "MySQL":
                    info.ConnectionId = result.ValueOrDefault(row, "id")?.ToString() ?? string.Empty;
                    info.UserName = result.ValueOrDefault(row, "USER")?.ToString() ?? string.Empty;
                    info.Database = result.ValueOrDefault(row, "DB")?.ToString() ?? string.Empty;
                    info.State = result.ValueOrDefault(row, "STATE")?.ToString() ?? string.Empty;
                    info.QueryText = result.ValueOrDefault(row, "query")?.ToString() ?? string.Empty;
                    if (double.TryParse(result.ValueOrDefault(row, "duration_sec")?.ToString(), out var mySec))
                        info.Duration = TimeSpan.FromSeconds(mySec);
                    break;
            }
            return info;
        }
        catch
        {
            return null;
        }
    }
}
