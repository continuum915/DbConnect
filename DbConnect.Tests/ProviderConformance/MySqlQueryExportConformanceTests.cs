using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.MySqlExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class MySqlQueryExportConformanceTests
{
    [MySqlExportFact]
    public async Task StreamsRawOrderedValuesBeyondCapOnOriginalTransactionWithCapturedParameters()
    {
        await using var session = await OpenAsync();
        Assert.False((await session.ExecuteQueryAsync("CREATE TEMPORARY TABLE items(id BIGINT, amount DECIMAL(27,9), stamp DATETIME(6), payload BLOB)")).HasError);
        await session.BeginTransactionAsync();
        for (var i = 0; i < 12; i++)
            Assert.False((await session.ExecuteQueryAsync($"INSERT INTO items VALUES ({i},123456789012345678.123456789,'2026-10-19 12:34:56.123456',X'0102')")).HasError);
        var parameters = new object?[] { 0, new byte[] { 1, 2 } };
        var result = await session.ExecuteQueryAsync("SELECT id, id, amount, stamp, payload, NULL AS optional, '' AS `empty`, 'NULL' AS literal, @p1 AS bound FROM items WHERE id>=@p0 ORDER BY id",
            new QueryExecutionOptions { MaxResultRows = 1, TimeoutSeconds = 7, Parameters = parameters });
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.True(result.IsTruncated);
        parameters[0] = 99;
        ((byte[])parameters[1]!)[0] = 99;
        var capture = Capture(session, result);
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in capture.Source.ReadRowsAsync(default)) rows.Add(row);
        Assert.Equal(12, rows.Count);
        Assert.Equal(["id", "id", "amount", "stamp", "payload", "optional", "empty", "literal", "bound"], capture.Source.Columns.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(0, 9), capture.Source.Columns.Select(c => c.Ordinal));
        Assert.Equal(typeof(decimal), capture.Source.Columns[2].ClrType);
        Assert.False(string.IsNullOrWhiteSpace(capture.Source.Columns[2].ProviderTypeName));
        Assert.Equal(11L, rows[^1][0]);
        Assert.Equal(0L, rows[0][1]);
        Assert.Equal(123456789012345678.123456789m, Assert.IsType<decimal>(rows[0][2]));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56).AddTicks(1234560), Assert.IsType<DateTime>(rows[0][3]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][4]));
        Assert.Null(rows[0][5]);
        Assert.Equal("", rows[0][6]);
        Assert.Equal("NULL", rows[0][7]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][8]));
        Assert.True(session.IsInTransaction);
        Assert.False((await session.ExecuteQueryAsync("SELECT id FROM items LIMIT 1")).HasError);
        await session.RollbackAsync();
        Assert.False(capture.IsCurrent());
        Assert.Empty((await session.ExecuteQueryAsync("SELECT id FROM items")).ObjectRows);
    }

    [MySqlExportTheory]
    [InlineData("SELECT 'semi;colon' AS value", true)]
    [InlineData("SELECT 1 /* comment */ AS value", true)]
    [InlineData("SELECT 1 INTO @rain_export_value", false)]
    [InlineData("SELECT 1 FOR UPDATE", false)]
    [InlineData("SELECT 1 LOCK IN SHARE MODE", false)]
    [InlineData("SELECT RAND()", false)]
    [InlineData("SELECT 1; SELECT 2", false)]
    public async Task OnlyEligibleRecordedResultsCanBeCaptured(string sql, bool eligible)
    {
        await using var session = await OpenAsync();
        var result = await session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.Equal(eligible, ((IQueryExportSession)session).CaptureQueryExport(result) != null);
    }
}
