using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// SQLite table statistics: exact row counts (SQLite keeps no estimate). Sizes, engine, times and collation do not
/// exist per table in SQLite, and comments are not stored, so they stay null.
/// </summary>
internal static class SqliteTableStatistics
{
    public static async Task<IReadOnlyList<TableStatistics>> ReadAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var names = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        }

        var statistics = new List<TableStatistics>(names.Count);
        foreach (var name in names)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
            long? rows;
            try { rows = (long?)await count.ExecuteScalarAsync(cancellationToken); }
            catch (SqliteException) { rows = null; } // a virtual table whose module is not loaded
            statistics.Add(new TableStatistics(name) { Rows = rows });
        }

        return statistics;
    }
}
