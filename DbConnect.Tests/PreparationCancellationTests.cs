using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class PreparationCancellationTests
{
    private const string Script = "SELECT @value; SELECT 'text' /* note */; SELECT 3;";
    private static readonly CancellationToken Cancelled = new(canceled: true);

    [Fact]
    public void DetectionStops() =>
        Assert.ThrowsAny<OperationCanceledException>(() => SqlParameterDetector.Detect(Script, Cancelled));

    [Fact]
    public void SubstitutionStops() =>
        Assert.ThrowsAny<OperationCanceledException>(() => SqlParameterDetector.SubstituteWithOffsets(Script,
            [new QueryParamEntry { Placeholder = "@value", Value = "x", DataType = "string" }], Cancelled));

    [Fact]
    public void StatementScanStops() =>
        Assert.ThrowsAny<OperationCanceledException>(() => SqlBatchParser.GetStatementSpans(Script, SqlBatchDialect.SqlServer, Cancelled));

    [Fact]
    public void CaretLookupStops() =>
        Assert.ThrowsAny<OperationCanceledException>(() => SqlBatchParser.FindStatementAt(Script, 5, SqlBatchDialect.SqlServer, Cancelled));

    [Fact]
    public void AnUncancelledTokenChangesNothing()
    {
        Assert.Equal(["@value"], SqlParameterDetector.Detect(Script, CancellationToken.None).Select(parameter => parameter.Placeholder));
        Assert.Equal(SqlBatchParser.GetStatementSpans(Script, SqlBatchDialect.SqlServer),
            SqlBatchParser.GetStatementSpans(Script, SqlBatchDialect.SqlServer, CancellationToken.None));
    }
}
