using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class QueryErrorNavigationTests
{
    [Fact]
    public void PostgreSqlCharacter42_MapsThroughStatementStart()
    {
        var statement = "SELECT " + new string(' ', 34) + "bad_token";
        var script = "SELECT 1;\n" + statement;
        var result = new QueryResult
        {
            ExecutedSql = statement,
            StatementStart = 10,
            Error = new QueryErrorInfo { Position = QueryErrorPositionExtractor.PostgreSql(42) }
        };

        var span = QueryErrorNavigator.FindToken(script, script, script, 0, result);

        Assert.Equal(new StatementSpan(51, 9), span);
        Assert.Equal("bad_token", script.Substring(span!.Value.Start, span.Value.Length));
    }

    [Fact]
    public void PostgreSqlCharacterPosition_FindsTokenInMiddleStatement()
    {
        const string script = "SELECT 1;\n  SELECT missing FROM things;";
        const string statement = "SELECT missing FROM things";
        var result = new QueryResult
        {
            ExecutedSql = statement,
            StatementStart = 12,
            Error = new QueryErrorInfo { Position = new QueryErrorPosition(Offset: 9) }
        };

        var span = QueryErrorNavigator.FindToken(script, script, script, 0, result);

        Assert.Equal(new StatementSpan(19, 7), span);
        Assert.Equal("missing", script.Substring(span!.Value.Start, span.Value.Length));
    }

    [Fact]
    public void SqlServerLineNumber_FindsTokenOnStatementLine()
    {
        const string script = "SELECT 1;\nSELECT foo\n  FROM missing;";
        const string statement = "SELECT foo\n  FROM missing";
        var result = new QueryResult
        {
            ExecutedSql = statement,
            StatementStart = 10,
            Error = new QueryErrorInfo { Position = new QueryErrorPosition(Line: 2) }
        };

        var span = QueryErrorNavigator.FindToken(script, script, script, 0, result);

        Assert.NotNull(span);
        Assert.Equal("FROM", script.Substring(span.Value.Start, span.Value.Length));
    }

    [Fact]
    public void SelectionPosition_IncludesSelectionStart()
    {
        const string script = "SELECT 1; SELECT missing";
        const string selection = "SELECT missing";
        var result = new QueryResult
        {
            ExecutedSql = selection,
            Error = new QueryErrorInfo { Position = new QueryErrorPosition(Offset: 7) }
        };

        var span = QueryErrorNavigator.FindToken(script, selection, selection, 10, result);

        Assert.Equal(new StatementSpan(17, 7), span);
    }

    [Fact]
    public void SubstitutedParameter_MapsDriverOffsetBackToPlaceholder()
    {
        const string source = "SELECT @name FROM missing";
        var substitution = SqlParameterDetector.SubstituteWithOffsets(source,
            [new QueryParamEntry { Placeholder = "@name", Value = "longer value" }]);
        var result = new QueryResult
        {
            ExecutedSql = substitution.Sql,
            Error = new QueryErrorInfo { Position = new QueryErrorPosition(Offset: 10) }
        };

        var span = QueryErrorNavigator.FindToken(source, source, substitution.Sql, 0,
            result, substitution.SourceOffsets);

        Assert.NotNull(span);
        Assert.Equal("@name", source.Substring(span.Value.Start, span.Value.Length));
    }

    [Fact]
    public void MultipleSubstitutions_MapLaterErrorToOriginalToken()
    {
        const string source = "SELECT @first, @second FROM missing";
        var substitution = SqlParameterDetector.SubstituteWithOffsets(source,
        [
            new QueryParamEntry { Placeholder = "@first", Value = "long first value" },
            new QueryParamEntry { Placeholder = "@second", Value = "another long value" }
        ]);
        var errorOffset = substitution.Sql.IndexOf("missing", StringComparison.Ordinal);
        var result = new QueryResult
        {
            ExecutedSql = substitution.Sql,
            Error = new QueryErrorInfo { Position = new QueryErrorPosition(Offset: errorOffset) }
        };

        var span = QueryErrorNavigator.FindToken(source, source, substitution.Sql, 0,
            result, substitution.SourceOffsets);

        Assert.NotNull(span);
        Assert.Equal("missing", source.Substring(span.Value.Start, span.Value.Length));
    }
}
