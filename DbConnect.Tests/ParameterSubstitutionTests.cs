using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class ParameterSubstitutionTests
{
    private static QueryParamEntry Text(string placeholder, string value) => new() { Placeholder = placeholder, Value = value, DataType = "string" };

    private static string Substitute(string sql, params QueryParamEntry[] parameters) =>
        SqlParameterDetector.SubstituteWithOffsets(sql, [.. parameters]).Sql;

    [Fact]
    public void ReplacesEveryOccurrenceOutsideLiteralsAndComments() =>
        Assert.Equal("SELECT 'x', 'x' FROM t", Substitute("SELECT @a, @a FROM t", Text("@a", "x")));

    [Fact]
    public void LeavesLiteralsAndCommentsAlone() =>
        Assert.Equal("SELECT '@a', \"@a\", 'x' /* @a */ -- @a\n",
            Substitute("SELECT '@a', \"@a\", @a /* @a */ -- @a\n", Text("@a", "x")));

    [Fact]
    public void AnIgnoredQuestionMarkDoesNotHideALaterPlaceholder() =>
        Assert.Equal("SELECT '?', 'long value', 'long value'", Substitute("SELECT '?', ?, ?", Text("?", "long value")));

    [Fact]
    public void QuestionMarksInsideValuesAreNotReplacedAgain() =>
        Assert.Equal("SELECT 'a?', 'a?'", Substitute("SELECT ?, ?", Text("?", "a?")));

    [Fact]
    public void AValueThatLooksLikeAnotherPlaceholderIsNotReplacedAgain() =>
        Assert.Equal("SELECT '@b', 'y'", Substitute("SELECT @a, @b", Text("@a", "@b"), Text("@b", "y")));

    [Fact]
    public void LongerPlaceholdersAreNotSplitByShorterOnes() =>
        Assert.Equal("SELECT 'short', 'long', @name2", Substitute("SELECT @name, @name_id, @name2", Text("@name", "short"), Text("@name_id", "long")));

    [Fact]
    public void ACastIsNotAColonPlaceholder() =>
        Assert.Equal("SELECT 'x', b::int", Substitute("SELECT :int, b::int", Text(":int", "x")));

    [Fact]
    public void NumberedQuestionMarksAreLeftForTheirOwnEntry() =>
        Assert.Equal("SELECT 'x', ?1", Substitute("SELECT ?, ?1", Text("?", "x")));

    [Theory]
    [InlineData("$1", "$10", "SELECT 'x', $10")]
    [InlineData("$1", "$1", "SELECT 'x', 'x'")]
    public void PositionalDollarsMatchWholeNumbers(string placeholder, string second, string expected) =>
        Assert.Equal(expected, Substitute($"SELECT $1, {second}", Text(placeholder, "x")));

    [Fact]
    public void ScriptsWithoutMatchesAreReturnedUnchanged()
    {
        const string sql = "SELECT 1";

        var result = SqlParameterDetector.SubstituteWithOffsets(sql, [Text("@a", "x")]);

        Assert.Same(sql, result.Sql);
        Assert.Equal(Enumerable.Range(0, sql.Length), result.SourceOffsets);
        Assert.Same(sql, SqlParameterDetector.SubstituteWithOffsets(sql, []).Sql);
    }

    [Fact]
    public void OffsetsMapEveryResultCharacterToItsSource()
    {
        const string sql = "@a, x @bb,@a";

        var result = SqlParameterDetector.SubstituteWithOffsets(sql, [Text("@a", "long"), new() { Placeholder = "@bb", Value = "", DataType = "number" }]);

        Assert.Equal("'long', x ,'long'", result.Sql);
        int[] expected = [0, 0, 0, 0, 0, 0, 2, 3, 4, 5, 9, 10, 10, 10, 10, 10, 10];
        Assert.Equal(expected, result.SourceOffsets);
        Assert.Equal(expected.Length, result.SourceOffsets.Count);
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(index => result.SourceOffsets[index]));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceOffsets[expected.Length]);
        Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceOffsets[-1]);
    }

    [Fact]
    public void CostGrowsWithTheScriptAndNotWithTheNumberOfMatches()
    {
        var sql = string.Concat(Enumerable.Repeat("INSERT INTO t VALUES (@value);\n", 5_000));
        var parameters = new List<QueryParamEntry> { Text("@value", "D'Angelo") };
        SqlParameterDetector.SubstituteWithOffsets(sql, parameters);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = SqlParameterDetector.SubstituteWithOffsets(sql, parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(sql.Replace("@value", "'D''Angelo'"), result.Sql);
        Assert.InRange(allocated, 0, 2 * sql.Length * sizeof(char) + 512 * 1024);
    }
}
