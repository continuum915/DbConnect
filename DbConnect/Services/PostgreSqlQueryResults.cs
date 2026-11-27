using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Reads native values and source metadata, then refreshes types after type DDL.</summary>
internal static class PostgreSqlQueryResults
{
    public static async Task ReadAsync(NpgsqlCommand command, QueryResult result, QueryExecutionOptions? options)
    {
        // KeyInfo asks Npgsql for source tables, columns and keys without converting values to text.
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, options?.CancellationToken ?? default))
            await DbReaderResults.ReadAsync(reader, result, options);
        await PostgreSqlConnections.RefreshTypesAsync(command.Connection!, command.CommandText);
    }

    public static async Task<List<QueryResult>> ReadAllAsync(NpgsqlCommand command, QueryExecutionOptions? options)
    {
        List<QueryResult> results;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, options?.CancellationToken ?? default))
            results = await DbReaderResults.ReadAllAsync(reader, options);
        await PostgreSqlConnections.RefreshTypesAsync(command.Connection!, command.CommandText);
        return results;
    }
}
