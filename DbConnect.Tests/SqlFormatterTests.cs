using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlFormatterTests
{
    [Fact]
    public void Format_PreservesLiteralsAndComments()
    {
        var sql = "select 'from where', \"order by\" -- order by\nfrom notes /* where select */ where body = 'it''s from';";

        Assert.Equal("SELECT 'from where', \"order by\" -- order by\nFROM notes /* where select */\nWHERE body = 'it''s from';", SqlFormatter.Format(sql).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(SqlBatchDialect.MySql, "select `order by` from `from where`", "SELECT `order by`\nFROM `from where`")]
    [InlineData(SqlBatchDialect.SqlServer, "select [order by] from [from where]", "SELECT [order by]\nFROM [from where]")]
    [InlineData(SqlBatchDialect.PostgreSql, "select $$from where; -- order by$$ from notes", "SELECT $$from where; -- order by$$\nFROM notes")]
    public void Format_PreservesDialectQuotes(SqlBatchDialect dialect, string sql, string expected)
    {
        Assert.Equal(expected, SqlFormatter.Format(sql, dialect).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_IndentsCtesAndSubqueries()
    {
        var sql = "with recent as (select id from orders where total > 10), active as (select id from recent) select id from active where id in (select id from customers)";
        Assert.Equal("""
            WITH recent AS (
                SELECT id
                FROM orders
                WHERE total > 10
            ),
            active AS (
                SELECT id
                FROM recent
            )
            SELECT id
            FROM active
            WHERE id IN (
                SELECT id
                FROM customers
            )
            """.ReplaceLineEndings("\n"), SqlFormatter.Format(sql).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_IndentsCaseArms()
    {
        Assert.Equal("""
            SELECT CASE
                WHEN score > 0 THEN
                'yes'
                ELSE
                'no'
            END AS result
            FROM scores
            """.ReplaceLineEndings("\n"), SqlFormatter.Format("select case when score > 0 then 'yes' else 'no' end as result from scores").ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(SqlBatchDialect.PostgreSql, "select $1::int from t where a>=2", "SELECT $1::int\nFROM t\nWHERE a >= 2")]
    [InlineData(SqlBatchDialect.MySql, "select @x, ? from t where a!=2", "SELECT @x, ?\nFROM t\nWHERE a != 2")]
    public void Format_PreservesOperatorsAndParameters(SqlBatchDialect dialect, string sql, string expected)
    {
        Assert.Equal(expected, SqlFormatter.Format(sql, dialect).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_PreservesSqlServerBatchSeparator()
    {
        Assert.Equal("SELECT 1\nGO\nSELECT 2", SqlFormatter.Format("select 1\nGO\nselect 2", SqlBatchDialect.SqlServer).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_PreservesPostgreSqlEscapeStringAndJsonOperator()
    {
        Assert.Equal("SELECT E'it\\'s from where', doc->>'name'\nFROM records",
            SqlFormatter.Format("select E'it\\'s from where', doc->>'name' from records", SqlBatchDialect.PostgreSql).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("//")]
    [InlineData("$$")]
    public void Format_PreservesMySqlCustomDelimiter(string delimiter)
    {
        var sql = $"DELIMITER {delimiter}\nCREATE PROCEDURE p() BEGIN SELECT 1; END{delimiter}\nDELIMITER ;";
        var formatted = SqlFormatter.Format(sql, SqlBatchDialect.MySql);

        Assert.Contains($"END {delimiter}", formatted);
        Assert.Single(SqlBatchParser.SplitStatements(formatted, SqlBatchDialect.MySql));
    }

    [Fact]
    public void Format_PreservesDollarInMySqlIdentifier()
    {
        Assert.Equal("SELECT foo$bar\nFROM t", SqlFormatter.Format("select foo$bar from t", SqlBatchDialect.MySql).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_UppercasesKeywordsAndBreaksClausesOntoSeparateLines()
    {
        var formatted = SqlFormatter.Format("select c.id, c.name from customers c where c.id = 1 order by c.name");

        Assert.Equal(
            """
            SELECT c.id, c.name
            FROM customers c
            WHERE c.id = 1
            ORDER BY c.name
            """.ReplaceLineEndings("\n"),
            formatted.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Format_ReturnsEmptyOrWhitespaceInputUnchanged(string? sql)
    {
        Assert.Equal(sql, SqlFormatter.Format(sql!));
    }
}
