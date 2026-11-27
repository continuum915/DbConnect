using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlStructureStreamingTests
{
    [Theory]
    [InlineData(SqlBatchDialect.Standard)]
    [InlineData(SqlBatchDialect.PostgreSql)]
    [InlineData(SqlBatchDialect.SqlServer)]
    [InlineData(SqlBatchDialect.MySql)]
    public void OperatorsAndQuotedParenthesesPreserveStructuralOffsets(SqlBatchDialect dialect)
    {
        const string sql = "(\n SELECT 12 ->> 'a)', x >= 2, y::int /* x\n y */\n)";
        var structure = SqlEditorStructure.Analyze(sql, dialect);

        Assert.Equal(((int Opening, int Closing)?)(0, sql.Length - 1), structure.FindMatchingParentheses(0));
        Assert.Equal(new[] { new StatementSpan(0, sql.Length), new(sql.IndexOf("/*", StringComparison.Ordinal), 10) }, structure.Foldings);
    }

    [Theory]
    [InlineData("GO -- (\n(\n1\n)", SqlBatchDialect.SqlServer)]
    [InlineData("DELIMITER //\n(\n1\n)//", SqlBatchDialect.MySql)]
    [InlineData("$body$( ignored )$body$\n(\n1\n)", SqlBatchDialect.PostgreSql)]
    [InlineData("/* outer\n /* inner */\n */\n(\n1\n)", SqlBatchDialect.PostgreSql)]
    public void DialectSyntaxDoesNotConsumeFollowingPair(string sql, SqlBatchDialect dialect)
    {
        var opening = sql.LastIndexOf('(');
        var closing = sql.LastIndexOf(')');
        Assert.Equal(((int Opening, int Closing)?)(opening, closing),
            SqlEditorStructure.Analyze(sql, dialect).FindMatchingParentheses(opening));
    }

    [Fact]
    public void AnalyzingManyNonStructuralTokensDoesNotAllocatePerToken()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT 123 ->> 'value'; ", 50_000));
        _ = SqlEditorStructure.Analyze("warmup");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var structure = SqlEditorStructure.Analyze(sql);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(structure.Foldings);
        Assert.True(allocated < 4096, $"Analysis allocated {allocated:N0} bytes.");
    }
}
