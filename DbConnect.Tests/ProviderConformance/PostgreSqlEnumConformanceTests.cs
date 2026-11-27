using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

public sealed class PostgreSqlEnumConformanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task EnumCreatedThroughQueryExecution_RetainsValuesAndSourceMetadata(bool useSession, bool useMultipleResults)
    {
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var provider = new PostgreSqlProvider();
        await using var session = useSession ? await provider.OpenSessionAsync(new ConnectionInfo
        {
            Provider = "PostgreSQL",
            ConnectionString = connectionString
        }) : null;
        var suffix = Guid.NewGuid().ToString("N");
        var type = $"rain_enum_{suffix}";
        var table = $"rain_enum_items_{suffix}";
        async Task<QueryResult> Execute(string sql, QueryExecutionOptions? options = null)
        {
            if (!useMultipleResults)
                return session is null ? await provider.ExecuteQueryAsync(connectionString, sql, options)
                    : await session.ExecuteQueryAsync(sql, options);
            var results = new List<QueryResult>();
            var stream = session is null ? provider.ExecuteQueryMultipleAsync(connectionString, sql, options)
                : session.ExecuteQueryMultipleAsync(sql, options);
            await foreach (var item in stream)
                results.Add(item);
            return Assert.Single(results);
        }
        try
        {
            Assert.False((await Execute($"CREATE TYPE {type} AS ENUM ('happy','sad');")).HasError);
            Assert.False((await Execute($"CREATE TABLE {table} (id INTEGER PRIMARY KEY, mood {type} NULL, amount DECIMAL(18,4), payload JSONB);")).HasError);
            Assert.False((await Execute($"INSERT INTO {table} VALUES (1, 'happy', 12.3456, '{{\"a\":1}}'), (2, NULL, NULL, NULL);")).HasError);
            var result = await Execute($"SELECT * FROM {table} ORDER BY id;");
            Assert.False(result.HasError, result.ErrorMessage);
            Assert.Equal("happy", result.ObjectRows[0][1]);
            Assert.Null(result.ObjectRows[1][1]);
            Assert.Equal(12.3456m, Assert.IsType<decimal>(result.ObjectRows[0][2]));
            using var payload = System.Text.Json.JsonDocument.Parse(Assert.IsType<string>(result.ObjectRows[0][3]));
            Assert.Equal(1, payload.RootElement.GetProperty("a").GetInt32());
            Assert.Equal($"public.{type}", result.ColumnInfos[1].ProviderTypeName);
            Assert.Equal(table, result.ColumnInfos[1].SourceTable);
            Assert.Equal("mood", result.ColumnInfos[1].SourceColumn);
            Assert.True(result.ColumnInfos[0].IsKey);
            var update = await Execute($"UPDATE {table} SET mood = @p0 WHERE id = 1;",
                new QueryExecutionOptions { Parameters = ["sad"] });
            Assert.False(update.HasError, update.ErrorMessage);
            Assert.Equal("sad", Assert.Single((await Execute($"SELECT mood FROM {table} WHERE id = 1;")).ObjectRows)[0]);
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {table};");
            await Execute($"DROP TYPE IF EXISTS {type};");
        }
    }
}
