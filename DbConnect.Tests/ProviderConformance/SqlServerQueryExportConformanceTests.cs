using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.ProviderConformance.SqlServerExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class SqlServerQueryExportConformanceTests
{
    [SqlServerExportFact]
    public async Task StreamsRawOrderedValuesBeyondCapWithBoundParametersAndOriginalSessionTransaction()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync();
        var setup = await session.ExecuteQueryAsync("""
            CREATE TABLE #items(id bigint PRIMARY KEY, amount decimal(27,9), flag bit,
                stamp datetime2(7), offsetstamp datetimeoffset(7), payload varbinary(8),
                optional nvarchar(10), empty nvarchar(10), literal nvarchar(10));
            INSERT INTO #items
            SELECT TOP (1200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))-1,
                123456789012345678.123456789, 1, '2026-10-19T12:34:56.1234567',
                '2026-10-19T12:34:56.1234567+05:30', 0x0102, NULL, '', 'NULL'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        Assert.False(setup.HasError, setup.Error?.Summary);
        var parameters = new object?[] { 0L, new byte[] { 1, 2 } };
        var result = await session.ExecuteQueryAsync("SELECT id, id, amount, flag, stamp, offsetstamp, payload, optional, empty, literal, @p1 AS bound FROM #items AS items WHERE id >= @p0 ORDER BY items.id",
            new QueryExecutionOptions { Parameters = parameters, MaxResultRows = 1, TimeoutSeconds = 7 });
        Assert.False(result.HasError, result.Error?.Summary);
        Assert.True(result.IsTruncated);
        parameters[0] = 2000L;
        ((byte[])parameters[1]!)[0] = 99;
        var capture = Capture(session, result);
        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var row in capture.Source.ReadRowsAsync(default)) rows.Add(row);
        Assert.Equal(1200, rows.Count);
        Assert.Equal(["id", "id", "amount", "flag", "stamp", "offsetstamp", "payload", "optional", "empty", "literal", "bound"], capture.Source.Columns.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(0, 11), capture.Source.Columns.Select(c => c.Ordinal));
        Assert.Equal(typeof(decimal), capture.Source.Columns[2].ClrType);
        Assert.Equal("decimal", capture.Source.Columns[2].ProviderTypeName);
        Assert.Equal(1199L, Assert.IsType<long>(rows[^1][0]));
        Assert.Equal(0L, Assert.IsType<long>(rows[0][1]));
        Assert.Equal(123456789012345678.123456789m, Assert.IsType<decimal>(rows[0][2]));
        Assert.True(Assert.IsType<bool>(rows[0][3]));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56).AddTicks(1234567), Assert.IsType<DateTime>(rows[0][4]));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 12, 34, 56, TimeSpan.FromHours(5.5)).AddTicks(1234567), Assert.IsType<DateTimeOffset>(rows[0][5]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][6]));
        Assert.Null(rows[0][7]);
        Assert.Equal("", rows[0][8]);
        Assert.Equal("NULL", rows[0][9]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(rows[0][10]));
        Assert.True(session.IsInTransaction);
        var state = await session.ExecuteQueryAsync("SELECT @@TRANCOUNT AS depth, XACT_STATE() AS state, COUNT(*) AS total FROM #items");
        Assert.False(state.HasError, state.Error?.Summary);
        Assert.Equal(new object?[] { 1, (short)1, 1200 }, Assert.Single(state.ObjectRows));
        await session.RollbackAsync();
        Assert.False(capture.IsCurrent());
        Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
    }
}
