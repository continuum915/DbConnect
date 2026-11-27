using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlEditorStructureTests
{
    [Fact]
    public void MultilineCommentsAndNestedParenthesesProduceOrderedFoldRanges()
    {
        var structure = SqlEditorStructure.Analyze("/* a\n b */\n(\n 1,\n (\n 2\n )\n)");

        Assert.Equal(new[] { new StatementSpan(0, 10), new(11, 16), new(18, 7) }, structure.Foldings);
    }

    [Theory]
    [InlineData("(1) /* comment */")]
    [InlineData("(1\n")]
    [InlineData("/* unfinished\n")]
    [InlineData("'/* quoted\n */ (\n)' -- (\n")]
    [InlineData("/*/")]
    public void SingleLineQuotedAndUnterminatedRegionsDoNotFold(string sql)
    {
        Assert.Empty(SqlEditorStructure.Analyze(sql).Foldings);
    }

    [Theory]
    [InlineData(7, 7, 15)]
    [InlineData(8, 7, 15)]
    [InlineData(12, 12, 14)]
    [InlineData(13, 12, 14)]
    [InlineData(14, 12, 14)]
    [InlineData(15, 12, 14)]
    [InlineData(16, 7, 15)]
    public void CaretBesideEitherParenthesisMatchesTheCorrectNestedPair(int caret, int opening, int closing)
    {
        var structure = SqlEditorStructure.Analyze("SELECT (1 + (2))");

        Assert.Equal(((int Opening, int Closing)?)(opening, closing), structure.FindMatchingParentheses(caret));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(17)]
    public void CaretAwayFromParenthesesOrOutsideTheDocumentHasNoMatch(int caret)
    {
        Assert.Null(SqlEditorStructure.Analyze("SELECT (1 + (2))").FindMatchingParentheses(caret));
    }

    [Theory]
    [InlineData("('|)')", SqlBatchDialect.Standard)]
    [InlineData("('it''s |)')", SqlBatchDialect.Standard)]
    [InlineData("(\"|)\")", SqlBatchDialect.Standard)]
    [InlineData("(`|)`) ", SqlBatchDialect.MySql)]
    [InlineData("([|)])", SqlBatchDialect.SqlServer)]
    [InlineData("(/* |) */ 1)", SqlBatchDialect.Standard)]
    [InlineData("(-- |)\n1)", SqlBatchDialect.Standard)]
    [InlineData("($body$ |) $body$)", SqlBatchDialect.PostgreSql)]
    [InlineData("(E'escaped\\' |)')", SqlBatchDialect.PostgreSql)]
    [InlineData("('escaped\\' |)')", SqlBatchDialect.MySql)]
    [InlineData("(\"escaped\\\" |)\")", SqlBatchDialect.MySql)]
    [InlineData("(1 # |)\n)", SqlBatchDialect.MySql)]
    public void ParenthesesInsideQuotesAndCommentsHaveNoMatch(string markedSql, SqlBatchDialect dialect)
    {
        var caret = markedSql.IndexOf('|');
        var structure = SqlEditorStructure.Analyze(markedSql.Replace("|", ""), dialect);

        Assert.Null(structure.FindMatchingParentheses(caret));
    }

    [Theory]
    [InlineData("(')')", SqlBatchDialect.Standard, 4)]
    [InlineData("('it''s )')", SqlBatchDialect.Standard, 10)]
    [InlineData("(\"(\")", SqlBatchDialect.Standard, 4)]
    [InlineData("(`)`)", SqlBatchDialect.MySql, 4)]
    [InlineData("([)])", SqlBatchDialect.SqlServer, 4)]
    [InlineData("(/* ) */ 1)", SqlBatchDialect.Standard, 10)]
    [InlineData("(-- )\n1)", SqlBatchDialect.Standard, 7)]
    [InlineData("($$ ) $$)", SqlBatchDialect.PostgreSql, 8)]
    public void QuotesAndCommentsDoNotConsumeTheEnclosingPair(string sql, SqlBatchDialect dialect, int closing)
    {
        Assert.Equal(((int Opening, int Closing)?)(0, closing),
            SqlEditorStructure.Analyze(sql, dialect).FindMatchingParentheses(0));
    }

    [Fact]
    public void MySqlHashCommentDoesNotConsumeTheEnclosingPair()
    {
        var structure = SqlEditorStructure.Analyze("(1 # )\n)", SqlBatchDialect.MySql);

        Assert.Equal(((int Opening, int Closing)?)(0, 7), structure.FindMatchingParentheses(0));
        Assert.Equal(new[] { new StatementSpan(0, 8) }, structure.Foldings);
    }

    [Theory]
    [InlineData(")(")]
    [InlineData("('")]
    [InlineData("(/* )")]
    public void UnmatchedParenthesesAndIncompleteTokensHaveNoMatch(string sql)
    {
        var structure = SqlEditorStructure.Analyze(sql);

        for (var caret = 0; caret <= sql.Length; caret++)
            Assert.Null(structure.FindMatchingParentheses(caret));
    }

    [Theory]
    [InlineData(SqlBatchDialect.PostgreSql)]
    [InlineData(SqlBatchDialect.SqlServer)]
    public void UnterminatedOuterCommentDoesNotFoldAfterNestedCommentCloses(SqlBatchDialect dialect)
    {
        var structure = SqlEditorStructure.Analyze("/* outer\n /* inner */", dialect);

        Assert.Empty(structure.Foldings);
    }
}
