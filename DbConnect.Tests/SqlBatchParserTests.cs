using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlBatchParserTests
{
    [Fact]
    public void SplitStatements_IgnoresSemicolonsInsideStringsAndComments()
    {
        var statements = SqlBatchParser.SplitStatements("""
            SELECT ';not a split';
            -- comment ; still same statement
            UPDATE Demo SET Value = 'semi;colon';
            /* block ; comment */
            DELETE FROM Demo WHERE Id = 1;
            """);

        Assert.Equal(3, statements.Count);
        Assert.Equal("SELECT ';not a split'", statements[0]);
        Assert.Equal("-- comment ; still same statement\nUPDATE Demo SET Value = 'semi;colon'", statements[1].Replace("\r", ""));
        Assert.Equal("/* block ; comment */\nDELETE FROM Demo WHERE Id = 1", statements[2].Replace("\r", ""));
    }

    [Fact]
    public void PostgreSql_DollarQuotedBodyIsOneStatement()
    {
        var statements = SqlBatchParser.SplitStatements(
            "CREATE FUNCTION f() RETURNS int AS $$ BEGIN PERFORM 1; RETURN 2; END; $$ LANGUAGE plpgsql; SELECT 1;",
            SqlBatchDialect.PostgreSql);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE FUNCTION", statements[0]);
        Assert.EndsWith("LANGUAGE plpgsql", statements[0]);
        Assert.Equal("SELECT 1", statements[1]);
    }

    [Fact]
    public void PostgreSql_TaggedDollarQuoteIgnoresInnerBareDollarQuotes()
    {
        var statements = SqlBatchParser.SplitStatements(
            "DO $body$ BEGIN EXECUTE $$ select 1; $$; END $body$; SELECT 2;",
            SqlBatchDialect.PostgreSql);

        Assert.Equal(2, statements.Count);
        Assert.EndsWith("END $body$", statements[0]);
    }

    [Fact]
    public void PostgreSql_PositionalParameterIsNotADollarQuote()
    {
        var statements = SqlBatchParser.SplitStatements("SELECT $1; SELECT $2;", SqlBatchDialect.PostgreSql);

        Assert.Equal(2, statements.Count);
    }

    [Fact]
    public void MySql_DelimiterRoutineIsOneStatement()
    {
        var statements = SqlBatchParser.SplitStatements(
            "DELIMITER //\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND//\nDELIMITER ;\nSELECT 3;",
            SqlBatchDialect.MySql);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE PROCEDURE p()", statements[0]);
        Assert.EndsWith("END", statements[0]);
        Assert.Equal("SELECT 3", statements[1]);
    }

    // A statement left without its terminator before a DELIMITER line was dropped, so Run and restore skipped it silently.
    [Fact]
    public void MySql_TextBeforeADelimiterLineIsAStatementNotDropped()
    {
        var statements = SqlBatchParser.SplitStatements("SELECT 1\nDELIMITER $$\nSELECT 2$$\nDELIMITER ;", SqlBatchDialect.MySql);

        Assert.Equal(["SELECT 1", "SELECT 2"], statements);
    }

    [Fact]
    public void MySql_BacktickIdentifiersDoNotSplit()
    {
        var statements = SqlBatchParser.SplitStatements(
            "SELECT 1 AS `a;b`; SELECT 2 AS `select ; from`, 3 AS `x``;y`;",
            SqlBatchDialect.MySql);

        Assert.Equal(2, statements.Count);
        Assert.Equal("SELECT 1 AS `a;b`", statements[0]);
        Assert.Equal("SELECT 2 AS `select ; from`, 3 AS `x``;y`", statements[1]);
    }

    [Fact]
    public void Dialects_AreSelectedThroughProviderCapabilities()
    {
        Assert.Equal(SqlBatchDialect.PostgreSql, new PostgreSqlProvider().Capabilities.BatchDialect);
        Assert.Equal(SqlBatchDialect.MySql, new MySqlProvider().Capabilities.BatchDialect);
        Assert.Equal(SqlBatchDialect.Sqlite, new SqliteProvider().Capabilities.BatchDialect);
        Assert.Equal(SqlBatchDialect.SqlServer, new SqlServerProvider().Capabilities.BatchDialect);
    }

    [Fact]
    public void SqlServer_LineAnchoredGoSplitsBatches()
    {
        var statements = SqlBatchParser.SplitStatements(
            "CREATE TABLE t (id int)\nGO\nINSERT INTO t VALUES (1)\n  go 3 -- repeat\nSELECT 'GO' AS x, 1 AS go_col\nGO",
            SqlBatchDialect.SqlServer);

        Assert.Equal(3, statements.Count);
        Assert.Equal("CREATE TABLE t (id int)", statements[0]);
        Assert.Equal("INSERT INTO t VALUES (1)", statements[1]);
        Assert.Equal("SELECT 'GO' AS x, 1 AS go_col", statements[2]);
    }

    private static string[] ExecutionUnits(string sql, bool inDriverTransaction = false) =>
        SqlBatchParser.GetExecutionSpans(sql, SqlBatchDialect.SqlServer, inDriverTransaction)
            .Select(span => sql.Substring(span.Start, span.Length))
            .ToArray();

    [Theory]
    [InlineData("SELECT 1\nGO 3\nSELECT 2")]
    [InlineData("SELECT 1\n  go\t3 -- repeat\nSELECT 2")]
    [InlineData("SELECT 1\r\nGO 3\r\nSELECT 2")]
    public void SqlServer_GoCountRepeatsItsBatch(string sql)
    {
        Assert.Equal(["SELECT 1", "SELECT 1", "SELECT 1", "SELECT 2"], ExecutionUnits(sql));
    }

    [Fact]
    public void SqlServer_GoCountOnLastLineRepeatsItsBatch()
    {
        Assert.Equal(["SELECT 1", "SELECT 1"], ExecutionUnits("SELECT 1\nGO 2"));
    }

    [Fact]
    public void SqlServer_GoCountWithNoBatchSubmitsNothing()
    {
        Assert.Empty(ExecutionUnits("GO 3\n"));
    }

    [Fact]
    public void SqlServer_GoCountRepeatsASplitBatchInOrder()
    {
        Assert.Equal(["INSERT t VALUES (1)", "COMMIT", "INSERT t VALUES (1)", "COMMIT"],
            ExecutionUnits("INSERT t VALUES (1); COMMIT;\nGO 2", inDriverTransaction: true));
    }

    [Theory]
    [InlineData("SELECT 1\nGO 0")]
    [InlineData("SELECT 1\nGO -1")]
    [InlineData("SELECT 1\nGO +2")]
    [InlineData("SELECT 1\nGO 99999999999")]
    [InlineData("SELECT 1\ngo5")]
    public void SqlServer_InvalidGoCountIsLeftForTheServerToReject(string sql)
    {
        Assert.Equal([sql], ExecutionUnits(sql));
    }

    [Fact]
    public void SqlServer_GoCountsAtTheCommandLimitAreAllowed()
    {
        Assert.Equal(SqlBatchParser.MaximumRepeatedCommands,
            SqlBatchParser.GetExecutionSpans($"SELECT 1\nGO {SqlBatchParser.MaximumRepeatedCommands}", SqlBatchDialect.SqlServer).Count);
    }

    [Theory]
    [InlineData("SELECT 1\nGO 2147483647")]
    [InlineData("SELECT 1\nGO 1000000\nSELECT 2\nGO 2")]
    public void SqlServer_GoCountsOverTheCommandLimitAreRefused(string sql)
    {
        Assert.Throws<TooManyRepeatedCommandsException>(() => ExecutionUnits(sql));
    }

    [Fact]
    public void SqlServer_ALargeScriptWithoutGoCountsHasNoCommandLimit()
    {
        var sql = string.Join("\nGO\n", Enumerable.Repeat("SELECT 1", SqlBatchParser.MaximumRepeatedCommands + 1));

        Assert.Equal(SqlBatchParser.MaximumRepeatedCommands + 1,
            SqlBatchParser.GetExecutionSpans(sql, SqlBatchDialect.SqlServer).Count);
    }

    [Fact]
    public void SqlServer_BeginEndBlockIsNotSplitAtInnerSemicolons()
    {
        var statements = SqlBatchParser.SplitStatements(
            "IF 1 = 1 BEGIN SELECT 1; SELECT CASE WHEN 1=1 THEN 2 END; END; SELECT 3;",
            SqlBatchDialect.SqlServer);

        Assert.Equal(2, statements.Count);
        Assert.EndsWith("END", statements[0]);
        Assert.Equal("SELECT 3", statements[1]);
    }

    [Fact]
    public void SqlServer_BeginTransactionDoesNotOpenABlock()
    {
        var statements = SqlBatchParser.SplitStatements(
            "BEGIN TRAN; UPDATE t SET a = 1; COMMIT;",
            SqlBatchDialect.SqlServer);

        Assert.Equal(3, statements.Count);
    }

    [Fact]
    public void SqlServer_NestedBlockCommentsDoNotConfuseSplitting()
    {
        var statements = SqlBatchParser.SplitStatements(
            "SELECT 1; /* outer /* inner ; */ still comment ; */ SELECT 2;",
            SqlBatchDialect.SqlServer);

        Assert.Equal(2, statements.Count);
        Assert.EndsWith("SELECT 2", statements[1]);
    }

    [Fact]
    public void Sqlite_TriggerBodyIsOneStatement()
    {
        var statements = SqlBatchParser.SplitStatements(
            "CREATE TRIGGER trg AFTER INSERT ON t BEGIN UPDATE t SET a = 1; INSERT INTO log VALUES (1); END; SELECT 1;",
            SqlBatchDialect.Sqlite);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE TRIGGER", statements[0]);
        Assert.EndsWith("END", statements[0]);
    }

    [Fact]
    public void Sqlite_BeginTransactionStatementsStillSplit()
    {
        var statements = SqlBatchParser.SplitStatements(
            "BEGIN; INSERT INTO t VALUES (1); COMMIT; BEGIN IMMEDIATE; END;",
            SqlBatchDialect.Sqlite);

        Assert.Equal(5, statements.Count);
    }

    [Fact]
    public void Sqlite_BlockCommentsDoNotNest()
    {
        var statements = SqlBatchParser.SplitStatements("/* a /* b */ SELECT 1; SELECT 2;", SqlBatchDialect.Sqlite);

        Assert.Equal(2, statements.Count);
    }
}
