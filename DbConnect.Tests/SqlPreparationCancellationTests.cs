using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlPreparationCancellationTests
{
    [Fact]
    public void PreCanceledPreparationRejectsEvenEmptyInput()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => SqlParameterDetector.Detect("", stop.Token));
        Assert.Throws<OperationCanceledException>(() => SqlParameterDetector.SubstituteWithOffsets("", [], stop.Token));
        Assert.Throws<OperationCanceledException>(() => SqlBatchParser.GetStatementSpans("", cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => SqlBatchParser.SplitStatements("", cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => QueryScriptPreparation.Resolve("", "", false, 0, 0,
            SqlBatchDialect.Standard, stop.Token));
    }

    [Theory]
    [InlineData("/*", "*/", SqlBatchDialect.Standard)]
    [InlineData("'", "'", SqlBatchDialect.Standard)]
    [InlineData("--", "\n", SqlBatchDialect.Standard)]
    [InlineData("$tag$", "$tag$", SqlBatchDialect.PostgreSql)]
    [InlineData("", "", SqlBatchDialect.Standard)]
    public void TokenizerChecksCancellationInsideLongTokens(string prefix, string suffix, SqlBatchDialect dialect)
    {
        using var stop = new CancellationTokenSource();
        var sql = "SELECT " + prefix + new string('x', 32_768) + suffix;
        using var tokens = SqlTokenizer.EnumerateRanges(sql, dialect, stop.Token).GetEnumerator();
        Assert.True(tokens.MoveNext());
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => tokens.MoveNext());
    }

    [Theory]
    [InlineData("@")]
    [InlineData(":")]
    [InlineData("$")]
    public void PlaceholderScannerChecksCancellationInsideLongNamesOrOrdinals(string prefix)
    {
        using var stop = new CancellationTokenSource();
        var sql = "? " + prefix + new string(prefix == "$" ? '1' : 'x', 32_768);
        using var tokens = SqlParameterTokens.Scan(sql, stop.Token).GetEnumerator();
        Assert.True(tokens.MoveNext());
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => tokens.MoveNext());
    }

    [Theory]
    [InlineData("  SELECT\r\n\t @VALUE  ", "select @value")]
    [InlineData("  \r\n\t ", "")]
    [InlineData("SELECT 'A  B'", "select 'a b'")]
    public void FingerprintKeepsExistingNormalization(string sql, string expected)
    {
        var service = new QueryParamContextService();
        Assert.Equal(expected, service.GetFingerprint(sql));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.GetFingerprint(sql, stop.Token));
    }

    [Fact]
    public void SharedParameterScannerPreservesUnicodeNamesAndDollarSuffixRules()
    {
        const string sql = "SELECT @aé, @aé, $1suffix, $10, :x_1, ?2";
        Assert.Equal(new[] { "@aé", ":x_1", "$1", "$10", "?2" },
            SqlParameterDetector.Detect(sql).Select(parameter => parameter.Placeholder));
        var result = SqlParameterDetector.Substitute(sql,
            [new QueryParamEntry { Placeholder = "@aé", Value = 7, DataType = "int" },
             new QueryParamEntry { Placeholder = "$1", Value = 8, DataType = "int" }]);
        Assert.Equal("SELECT 7, 7, $1suffix, $10, :x_1, ?2", result);
    }
}
