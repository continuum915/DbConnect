using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

// Capture policy only; these controlled results do not verify Microsoft.Data.SqlClient.
public sealed class SqlServerQueryExportEligibilityTests
{
    [Theory]
    [InlineData("SELECT TOP (2) [value] FROM [items]", true)]
    [InlineData("SELECT @p0 AS [value]", true)]
    [InlineData("SELECT 'DELETE; @value=1' AS [value]", true)]
    [InlineData("SELECT 1 /* outer /* nested */ comment */ AS [value]", true)]
    [InlineData("SELECT 1 AS [value] UNION ALL SELECT 2", true)]
    [InlineData("SELECT [value] FROM (SELECT 1 AS [value]) AS t", true)]
    [InlineData("SELECT @value = 1", false)]
    [InlineData("SELECT @value += 1", false)]
    [InlineData("SELECT 1 INTO #exported", false)]
    [InlineData("SELECT 1 DELETE FROM items", false)]
    [InlineData("SELECT 1 EXEC some_procedure", false)]
    [InlineData("SELECT 1 SET NOCOUNT ON", false)]
    [InlineData("SELECT 1 CHECKPOINT", false)]
    [InlineData("SELECT 1 AS value SETUSER 'dbo'", false)]
    [InlineData("SELECT 1 AS value REVERT", false)]
    [InlineData("SELECT 1 WRITETEXT items.payload @ptr 'changed'", false)]
    [InlineData("SELECT 1 UPDATETEXT items.payload @ptr 0 0 'changed'", false)]
    [InlineData("SELECT 1 MOVE CONVERSATION @handle TO @group", false)]
    [InlineData("SELECT 1 GET CONVERSATION GROUP @group FROM queue_name", false)]
    [InlineData("SELECT 1 SELECT 2", false)]
    [InlineData("SELECT 1; SELECT 2", false)]
    [InlineData("SELECT 1\nGO\nSELECT 2", false)]
    [InlineData("SELECT [value] FROM items WITH (UPDLOCK)", false)]
    [InlineData("SELECT [value] FROM items HOLDLOCK", false)]
    [InlineData("SELECT NEXT VALUE FOR sequence_name", false)]
    [InlineData("SELECT dbo.unknown_function()", false)]
    [InlineData("WITH x AS (SELECT 1 AS value) SELECT value FROM x", false)]
    public async Task RecordedResultsOnlyOfferConservativeSingleReadReruns(string sql, bool eligible)
    {
        await using var session = await ConnectionDbSession.OpenAsync(new SqlServerProvider(),
            new SqliteConnection("Data Source=:memory:"),
            (_, _, _, _) => Task.FromResult(Result()),
            (_, _, _, _) => Task.FromResult(new List<QueryResult> { Result() }), default,
            queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
        var result = await session.ExecuteQueryAsync(sql);
        Assert.Equal(eligible, ((IQueryExportSession)session).CaptureQueryExport(result) != null);
    }

    private static QueryResult Result() => new()
    {
        ColumnInfos = [new ColumnInfo { Name = "value", Ordinal = 0, ClrType = typeof(long) }],
        ObjectRows = [[1L]]
    };
}
