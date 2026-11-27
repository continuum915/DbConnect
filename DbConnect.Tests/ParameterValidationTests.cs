using DbConnect.Models;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public class ParameterValidationTests
{
    [Fact]
    public void OracleValidatesWithoutRetainingFullSqlAndOffsetCopies()
    {
        var fixture = RepresentativeImport.Generate(65_536, "actual-parameters");
        var result = SqlParameterDetector.SubstituteWithOffsets(fixture.Sql,
            [new QueryParamEntry { Placeholder = "@value", Value = "D'Angelo" }]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        ParameterValidation.Validate(fixture, result);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < fixture.Sql.Length,
            $"Oracle allocated {allocated:N0} bytes for {fixture.Sql.Length:N0} characters.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OracleRejectsLateSqlOrOffsetCorruption(bool corruptSql)
    {
        var fixture = RepresentativeImport.Generate(8192, "actual-parameters");
        var result = SqlParameterDetector.SubstituteWithOffsets(fixture.Sql,
            [new QueryParamEntry { Placeholder = "@value", Value = "D'Angelo" }]);
        var offsets = result.SourceOffsets.ToArray();
        var sql = result.Sql;
        if (corruptSql) sql = sql[..^1] + "!";
        else offsets[^1] = 0;

        Assert.Throws<InvalidDataException>(() => ParameterValidation.Validate(fixture, new(sql, offsets)));
    }
}
