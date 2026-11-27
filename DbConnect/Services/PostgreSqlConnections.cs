using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace DbConnect.Services;

/// <summary>Shares PostgreSQL pools configured to read database-defined enums as strings.</summary>
internal static class PostgreSqlConnections
{
    private static readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> Sources = new();

    public static NpgsqlConnection Create(string connectionString) => Source(connectionString).CreateConnection();

    private static NpgsqlDataSource Source(string connectionString) => Sources.GetOrAdd(connectionString,
        key => new Lazy<NpgsqlDataSource>(() => new NpgsqlDataSourceBuilder(key).EnableUnmappedTypes().Build())).Value;

    public static async Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = Create(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var affected = await command.ExecuteNonQueryAsync();
        await RefreshTypesAsync(connection, sql);
        return affected;
    }

    public static Task RefreshTypesAsync(NpgsqlConnection connection, string sql) => ChangesTypes(sql)
        ? connection.ReloadTypesAsync()
        : Task.CompletedTask;

    private static bool ChangesTypes(string sql)
    {
        var words = SqlTokenizer.Tokenize(sql, SqlBatchDialect.PostgreSql)
            .Where(token => token.Kind != SqlTokenKind.Comment).ToArray();
        for (var i = 0; i + 1 < words.Length; i++)
            if ((words[i].Text.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                || words[i].Text.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
                || words[i].Text.Equals("DROP", StringComparison.OrdinalIgnoreCase))
                && words[i + 1].Text.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
