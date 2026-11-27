using System.Linq;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlParameterDetectorTests
{
    [Fact]
    public void DetectsRealParametersWhileIgnoringCommentsAndQuotedText()
    {
        const string sql = "SELECT @name, :value, $2, ?, ?3, @name; " +
            "'O''Brien @hidden :hidden $9 ?' \"@quoted\" -- @comment :comment $8 ?\r\n" +
            "/* @block :block $7 ? */ SELECT @other";

        Assert.Equal(new[] { "@name", "@other", ":value", "$2", "?", "?3" },
            SqlParameterDetector.Detect(sql).Select(parameter => parameter.Placeholder));
    }

    [Theory]
    [InlineData("SELECT @real; -- @hidden")]
    [InlineData("SELECT @real; /* @hidden")]
    [InlineData("SELECT @real; 'unterminated @hidden")]
    [InlineData("SELECT @real; \"unterminated @hidden")]
    public void IgnoresParametersInUnterminatedCommentsAndQuotes(string sql)
    {
        Assert.Equal(new[] { "@real" }, SqlParameterDetector.Detect(sql).Select(parameter => parameter.Placeholder));
    }

    [Fact]
    public void PreservesCurrentDeclareAndSetExclusionBehavior()
    {
        const string sql = "DECLARE @local int; SET @other = 1; SELECT @input, @input";
        Assert.Equal(new[] { "@input", "@other" },
            SqlParameterDetector.Detect(sql).Select(parameter => parameter.Placeholder));
    }

    [Theory]
    [InlineData("SET @local = 1; SELECT @input", "@input")]
    [InlineData("upset @input", "@input")]
    [InlineData("SELECT '@ignored'; DECLARE @local int; SELECT @input", "@input")]
    public void DetectsParametersAroundLocalVariableExclusions(string sql, string expected)
    {
        Assert.Equal(new[] { expected }, SqlParameterDetector.Detect(sql).Select(parameter => parameter.Placeholder));
    }

    [Fact]
    public void RepresentativeImportContainsNoDetectedParameters()
    {
        var fixture = RepresentativeImport.Generate(128 * 1024);
        Assert.Empty(SqlParameterDetector.Detect(fixture.Sql));
    }
}
