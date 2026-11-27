using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

// Capability contract checks only; real MySqlConnector coverage is in ProviderConformance.
public sealed class MySqlQueryExportEligibilityTests
{
    [Theory]
    [InlineData("SELECT @p0 AS value", true)]
    [InlineData("SELECT 'text := value' AS value", true)]
    [InlineData("SELECT 1 LOCK IN SHARE MODE", false)]
    [InlineData("SELECT @value := 1", false)]
    [InlineData("SELECT 1--1 + GET_LOCK('rain_export', 0) AS value", false)]
    [InlineData("SELECT 1 /*! INTO OUTFILE '/tmp/export' */", false)]
    [InlineData("SELECT 1 /*M! INTO OUTFILE '/tmp/export' */", false)]
    [InlineData("SELECT 1 /* ordinary comment */", true)]
    public async Task RecordedResultEligibilityRejectsMySqlSideEffects(string sql, bool eligible)
    {
        var result = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "value", Ordinal = 0, ClrType = typeof(long) }],
            ObjectRows = [[1L]]
        };
        await using var session = await ConnectionDbSession.OpenAsync(new MySqlProvider(),
            new SqliteConnection("Data Source=:memory:"),
            (_, _, _, _) => Task.FromResult(result),
            (_, _, _, _) => Task.FromResult(new List<QueryResult> { result }), default,
            queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
        var selected = await session.ExecuteQueryAsync(sql);
        Assert.Equal(eligible, ((IQueryExportSession)session).CaptureQueryExport(selected) != null);
    }
}
