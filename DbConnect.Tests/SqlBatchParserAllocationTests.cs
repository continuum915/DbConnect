using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlBatchParserAllocationTests
{
    [Fact]
    public void StatementSpansDoNotMaterializeAllTokenText()
    {
        const int count = 32_768;
        var sql = string.Concat(Enumerable.Repeat("SELECT 1;", count));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var spans = SqlBatchParser.GetStatementSpans(sql, SqlBatchDialect.SqlServer);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, spans.Count);
        Assert.Equal(new StatementSpan(0, 8), spans[0]);
        Assert.Equal(new StatementSpan((count - 1) * 9, 8), spans[^1]);
        Assert.True(allocated < sql.Length * 8L + 1_048_576,
            $"Span scanning allocated {allocated:N0} bytes for {sql.Length:N0} characters.");
    }
}
