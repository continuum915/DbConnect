using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DbConnect.Services;

internal static class SqliteRebuildExecution
{
    public const string Marker = "-- DbConnect SQLite table rebuild";

    public static async Task<int> ExecuteAsync(SqliteConnection connection, string sql)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // RAISE(ROLLBACK) already rolls back integrity failures. Other failures still need cleanup.
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            try { await rollback.ExecuteNonQueryAsync(); }
            catch (SqliteException) { /* There may be no active transaction. */ }
            throw;
        }
        finally
        {
            await using var restore = connection.CreateCommand();
            restore.CommandText = "PRAGMA foreign_keys=ON;";
            await restore.ExecuteNonQueryAsync();
        }
    }
}
