using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Shared public session contract on a real local connection; not PostgreSQL driver verification.</summary>
public sealed class QueryExportSessionContractTests
{
    private static Task<IDbSession> OpenCapturePolicySessionAsync()
    {
        // Controlled executor: capture policy only, not PostgreSQL driver behavior.
        static Task<QueryResult> Execute(SqliteConnection _, DbTransaction? transaction, string query, QueryExecutionOptions? options) =>
            Task.FromResult(new QueryResult { ColumnInfos = [new ColumnInfo { Name = "value" }], ObjectRows = [[1L]] });
        return ConnectionDbSession.OpenAsync(new PostgreSqlProvider(), new SqliteConnection("Data Source=:memory:"),
            Execute, async (connection, transaction, query, options) => [await Execute(connection, transaction, query, options)],
            default, queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("memory")]
    [InlineData("cycle")]
    public async Task NonReplayableMutableParametersDoNotOfferCapture(string kind)
    {
        using var stream = new MemoryStream([1, 2]);
        var cycle = new object?[1];
        cycle[0] = cycle;
        object value = kind switch { "stream" => stream, "memory" => new ReadOnlyMemory<byte>(new byte[] { 1, 2 }), _ => cycle };
        await using var session = await OpenCapturePolicySessionAsync();
        var result = await session.ExecuteQueryAsync("SELECT @p0 AS value", new QueryExecutionOptions { Parameters = [value] });
        Assert.Null(((IQueryExportSession)session).CaptureQueryExport(result));
    }

    [Theory]
    [InlineData("SELECT $$semi;colon$$ AS value", true)]
    [InlineData("SELECT 1 /* outer /* nested */ comment */ AS value", true)]
    [InlineData("SELECT 1 INTO exported_table", false)]
    [InlineData("SELECT value FROM items FOR UPDATE", false)]
    [InlineData("SELECT value FROM items FOR SHARE", false)]
    [InlineData("SELECT nextval('sequence_name')", false)]
    [InlineData("SELECT 1; DELETE FROM items", false)]
    public async Task PostgreSqlEligibilityUsesItsDialectAndExcludesWritesAndLocks(string sql, bool eligible)
    {
        await using var session = await OpenCapturePolicySessionAsync();
        var result = await session.ExecuteQueryAsync(sql);
        var capture = Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result);
        Assert.Equal(eligible, capture != null);
    }

    [Fact]
    public async Task RerunAppliesTheSameParameterConfigurationAsOriginalExecution()
    {
        static void Configure(DbParameter parameter) => parameter.Value = long.Parse((string)parameter.Value!, CultureInfo.InvariantCulture);
        static async Task<QueryResult> Execute(SqliteConnection connection, DbTransaction? transaction, string sql, QueryExecutionOptions? options)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = (SqliteTransaction?)transaction;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p0";
            parameter.Value = options!.Parameters![0]!;
            Configure(parameter);
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new QueryResult();
            await DbReaderResults.ReadAsync(reader, result, options);
            return result;
        }
        await using var session = await ConnectionDbSession.OpenAsync(new SqliteProvider(), new SqliteConnection("Data Source=:memory:"),
            Execute, async (connection, transaction, sql, options) => [await Execute(connection, transaction, sql, options)],
            default, configureParameter: Configure, queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
        var result = await session.ExecuteQueryAsync("SELECT @p0 AS value", new QueryExecutionOptions { Parameters = ["7"] });
        Assert.Equal(7L, Assert.Single(result.ObjectRows)[0]);
        var capture = Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result);
        Assert.NotNull(capture);
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in capture.Source.ReadRowsAsync(default)) rows.Add(row);
        Assert.Equal(7L, Assert.Single(rows)[0]);
    }
}
