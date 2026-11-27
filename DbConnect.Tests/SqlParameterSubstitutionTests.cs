using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlParameterSubstitutionTests
{
    [Fact]
    public void DoesNotSubstitutePlaceholderTextIntroducedByAReplacement()
    {
        var result = SqlParameterDetector.Substitute("SELECT @first, @other",
        [
            Text("@first", "@other"),
            Text("@other", "changed")
        ]);

        Assert.Equal("SELECT '@other', 'changed'", result);
    }

    [Fact]
    public void ReplacesRepeatedCompleteNamesWithoutReplacingLongerTokens()
    {
        var result = SqlParameterDetector.Substitute(
            "SELECT @name, @name_id, @name, @name_extra, :value, :value_more",
        [
            Number("@name", 1),
            Number("@name_id", 2),
            Number(":value", 3)
        ]);

        Assert.Equal("SELECT 1, 2, 1, @name_extra, 3, :value_more", result);
    }

    [Fact]
    public void DistinguishesDollarOrdinals()
    {
        var result = SqlParameterDetector.Substitute("SELECT $1, $10, $100, $1",
            [Number("$1", 1), Number("$10", 10)]);

        Assert.Equal("SELECT 1, 10, $100, 1", result);
    }

    [Fact]
    public void DistinguishesBareAndNumberedQuestionParameters()
    {
        var result = SqlParameterDetector.Substitute("SELECT ?, ?3, ?30, ?, ?3",
            [Number("?", 1), Number("?3", 3)]);

        Assert.Equal("SELECT 1, 3, ?30, 1, 3", result);
    }

    [Theory]
    [InlineData("@value")]
    [InlineData(":value")]
    [InlineData("$1")]
    [InlineData("?")]
    [InlineData("?3")]
    public void SkipsCommentsAndEscapedQuotedText(string placeholder)
    {
        var ignored = $"'O''Brien {placeholder}' \"escaped\"\"{placeholder}\" " +
            $"-- {placeholder}\r\n/* {placeholder} */ ";
        var result = SqlParameterDetector.Substitute(ignored + "SELECT " + placeholder,
            [Number(placeholder, 7)]);

        Assert.Equal(ignored + "SELECT 7", result);
    }

    [Theory]
    [InlineData("-- @value")]
    [InlineData("/* @value")]
    [InlineData("'unterminated @value")]
    [InlineData("\"unterminated @value")]
    public void LeavesUnterminatedCommentsAndQuotesUntouched(string ignored)
    {
        var result = SqlParameterDetector.Substitute("SELECT @value; " + ignored,
            [Number("@value", 7)]);

        Assert.Equal("SELECT 7; " + ignored, result);
    }

    [Fact]
    public void EscapesApostrophesInReplacementValues()
    {
        var result = SqlParameterDetector.Substitute("SELECT @value",
            [Text("@value", "O'Brien")]);

        Assert.Equal("SELECT 'O''Brien'", result);
    }

    [Fact]
    public void MapsCharactersBeforeWithinAndAfterExpandingAndShrinkingReplacements()
    {
        const string source = "SELECT @a, @long_name FROM missing";
        var result = SqlParameterDetector.SubstituteWithOffsets(source,
            [Text("@a", "O'Brien"), Number("@long_name", 7)]);
        const string expectedSql = "SELECT 'O''Brien', 7 FROM missing";
        var firstStart = source.IndexOf("@a", StringComparison.Ordinal);
        var secondStart = source.IndexOf("@long_name", StringComparison.Ordinal);
        var expectedOffsets = Enumerable.Range(0, firstStart)
            .Concat(Enumerable.Repeat(firstStart, "'O''Brien'".Length))
            .Concat(Enumerable.Range(firstStart + 2, secondStart - firstStart - 2))
            .Concat(Enumerable.Repeat(secondStart, 1))
            .Concat(Enumerable.Range(secondStart + "@long_name".Length,
                source.Length - secondStart - "@long_name".Length));

        Assert.Equal(expectedSql, result.Sql);
        Assert.Equal(expectedSql.Length, result.SourceOffsets.Count);
        Assert.Equal(expectedOffsets, result.SourceOffsets);
    }

    [Fact]
    public void MapsRepeatedReplacementsToTheirOwnOriginalPositions()
    {
        const string source = "@a + @a";
        var result = SqlParameterDetector.SubstituteWithOffsets(source, [Text("@a", "xx")]);

        Assert.Equal("'xx' + 'xx'", result.Sql);
        Assert.Equal(new[] { 0, 0, 0, 0, 2, 3, 4, 5, 5, 5, 5 }, result.SourceOffsets);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    [InlineData("SELECT 1")]
    public void PreservesSqlAndIdentityOffsetsWithoutParameters(string sql)
    {
        var result = SqlParameterDetector.SubstituteWithOffsets(sql, []);

        Assert.Equal(sql, result.Sql);
        Assert.Equal(Enumerable.Range(0, sql.Length), result.SourceOffsets);
    }

    private static QueryParamEntry Text(string placeholder, string value)
        => new() { Placeholder = placeholder, Value = value, DataType = "string" };

    private static QueryParamEntry Number(string placeholder, int value)
        => new() { Placeholder = placeholder, Value = value, DataType = "int" };
}
