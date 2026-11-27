using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class ResultCopyFormatterTests
{
    private static readonly string NL = Environment.NewLine;

    private static string Format(ResultCopyOptions options, params object?[][] rows) =>
        ResultCopyFormatter.FormatRows(["id", "name"], rows, options);

    [Fact]
    public void Defaults_AreTabSeparatedWithHeaderAndUnquoted()
    {
        var text = Format(new ResultCopyOptions(), [1, "Alpha"], [2, null]);

        Assert.Equal($"id\tname{NL}1\tAlpha{NL}2\tNULL", text);
    }

    [Fact]
    public void IncludeHeaderOff_OmitsTheHeaderLine()
    {
        var text = Format(new ResultCopyOptions { IncludeHeader = false }, [1, "Alpha"]);

        Assert.Equal("1\tAlpha", text);
    }

    [Fact]
    public void HeaderOnly_WhenThereAreNoRows()
    {
        Assert.Equal("id\tname", Format(new ResultCopyOptions()));
    }

    [Theory]
    [InlineData(CsvDelimiter.Comma, "id,name")]
    [InlineData(CsvDelimiter.Semicolon, "id;name")]
    [InlineData(CsvDelimiter.Pipe, "id|name")]
    public void Delimiter_SeparatesHeaderAndValues(CsvDelimiter delimiter, string expectedHeader)
    {
        var text = Format(new ResultCopyOptions { Delimiter = delimiter }, [1, "Alpha"]);

        var separator = expectedHeader[2];
        Assert.Equal($"{expectedHeader}{NL}1{separator}Alpha", text);
    }

    [Fact]
    public void QuoteCharacter_QuotesOnlyValuesThatNeedIt_AndDoublesEmbeddedQuotes()
    {
        var options = new ResultCopyOptions { Delimiter = CsvDelimiter.Comma, Quote = CopyQuoteCharacter.DoubleQuote };

        var text = Format(options, [1, "Smith, John"], [2, "say \"hi\""], [3, "two\nlines"], [4, "plain"]);

        Assert.Equal(
            $"id,name{NL}1,\"Smith, John\"{NL}2,\"say \"\"hi\"\"\"{NL}3,\"two\nlines\"{NL}4,plain",
            text);
    }

    [Fact]
    public void SingleQuoteCharacter_WithAlwaysQuote_QuotesEveryHeaderAndValue()
    {
        var options = new ResultCopyOptions { Quote = CopyQuoteCharacter.SingleQuote, AlwaysQuote = true };

        var text = Format(options, [1, "O'Brien"]);

        Assert.Equal($"'id'\t'name'{NL}'1'\t'O''Brien'", text);
    }

    [Fact]
    public void NoQuoteCharacter_LeavesValuesContainingTheDelimiterUntouched()
    {
        var options = new ResultCopyOptions { Delimiter = CsvDelimiter.Comma, AlwaysQuote = true };

        Assert.Equal("1,a,b", Format(options with { IncludeHeader = false }, [1, "a,b"]));
    }

    [Fact]
    public void SqlLiterals_QuoteStringsByType_AndWriteNullAsKeyword()
    {
        var options = new ResultCopyOptions { Delimiter = CsvDelimiter.Comma, IncludeHeader = false, SqlLiterals = true };

        var text = ResultCopyFormatter.FormatRows(
            ["a", "b", "c", "d", "e", "f", "g"],
            [[42, 3.5m, "O'Brien", null, true, new DateTime(2026, 1, 2, 3, 4, 5), new byte[] { 0xAB, 0x01 }]],
            options);

        Assert.Equal("42,3.5,'O''Brien',NULL,TRUE,'2026-01-02 03:04:05',X'AB01'", text);
    }

    [Fact]
    public void SqlLiterals_DistinguishTheStringNullFromSqlNull()
    {
        var options = new ResultCopyOptions { IncludeHeader = false, SqlLiterals = true };

        Assert.Equal("'NULL'\tNULL", Format(options, ["NULL", null]));
    }

    [Fact]
    public void SqlLiterals_IgnoreTheQuoteCharacterForValues_ButHeadersStillHonourIt()
    {
        var options = new ResultCopyOptions
        {
            Quote = CopyQuoteCharacter.DoubleQuote,
            AlwaysQuote = true,
            SqlLiterals = true
        };

        Assert.Equal($"\"id\"\t\"name\"{NL}1\t'Alpha'", Format(options, [1, "Alpha"]));
    }

    [Fact]
    public void FormatCell_HonoursSqlLiteralsAndAlwaysQuote_ButNotDelimiterRules()
    {
        Assert.Equal("a,b", ResultCopyFormatter.FormatCell("a,b", new ResultCopyOptions { Quote = CopyQuoteCharacter.DoubleQuote, Delimiter = CsvDelimiter.Comma }));
        Assert.Equal("\"a\"", ResultCopyFormatter.FormatCell("a", new ResultCopyOptions { Quote = CopyQuoteCharacter.DoubleQuote, AlwaysQuote = true }));
        Assert.Equal("'it''s'", ResultCopyFormatter.FormatCell("it's", new ResultCopyOptions { SqlLiterals = true }));
        Assert.Equal("NULL", ResultCopyFormatter.FormatCell(null, new ResultCopyOptions()));
    }

    [Fact]
    public void SqlLiterals_KeepFractionalSeconds_AndQuoteNonFiniteFloats()
    {
        Assert.Equal("'2026-01-02 03:04:05.123'", SqlLiteral.Format(new DateTime(2026, 1, 2, 3, 4, 5, 123)));
        Assert.Equal("'NaN'", SqlLiteral.Format(double.NaN));
        Assert.Equal("'Infinity'", SqlLiteral.Format(float.PositiveInfinity));
    }

    [Fact]
    public void SqlLiterals_FollowTheDialect_ForBooleansAndBinary()
    {
        var sqlServer = new ResultCopyOptions { SqlLiterals = true, Dialect = new SqlServerProvider().Capabilities };
        var postgres = new ResultCopyOptions { SqlLiterals = true, Dialect = new PostgreSqlProvider().Capabilities };

        Assert.Equal("1", ResultCopyFormatter.FormatCell(true, sqlServer));
        Assert.Equal("0xAB01", ResultCopyFormatter.FormatCell(new byte[] { 0xAB, 0x01 }, sqlServer));
        Assert.Equal("TRUE", ResultCopyFormatter.FormatCell(true, postgres));
        Assert.Equal(@"'\xAB01'::bytea", ResultCopyFormatter.FormatCell(new byte[] { 0xAB, 0x01 }, postgres));
    }

    [Fact]
    public void SqlLiterals_FollowTheDialect_ForTextAndTimestamps()
    {
        var sqlServer = new ResultCopyOptions { SqlLiterals = true, Dialect = new SqlServerProvider().Capabilities };
        var mySql = new ResultCopyOptions { SqlLiterals = true, Dialect = new MySqlProvider().Capabilities };
        var postgres = new ResultCopyOptions { SqlLiterals = true, Dialect = new PostgreSqlProvider().Capabilities };
        var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 500, TimeSpan.FromHours(5.5));

        Assert.Equal(@"N'日本 it''s \'", ResultCopyFormatter.FormatCell(@"日本 it's \", sqlServer));
        Assert.Equal("'2026-01-02T03:04:05.5+05:30'", ResultCopyFormatter.FormatCell(stamp, sqlServer));
        Assert.Equal("'2026-01-02T03:04:05'", ResultCopyFormatter.FormatCell(new DateTime(2026, 1, 2, 3, 4, 5), sqlServer));
        Assert.Equal(@"'C:\\temp it''s \\'''", ResultCopyFormatter.FormatCell(@"C:\temp it's \'", mySql));
        Assert.Equal(@"'C:\temp'", ResultCopyFormatter.FormatCell(@"C:\temp", postgres));
        Assert.Equal("'-838:59:59'", ResultCopyFormatter.FormatCell(-new TimeSpan(838, 59, 59), mySql));
        Assert.Equal("'100:00:00.5'", ResultCopyFormatter.FormatCell(new TimeSpan(0, 100, 0, 0, 500), mySql));
        Assert.Equal("'2026-01-02 03:04:05.5+05:30'", ResultCopyFormatter.FormatCell(stamp, postgres));
    }

    [Fact]
    public void SqlLiterals_WithoutADialect_UseTrueFalseAndHexStringLiterals()
    {
        Assert.Equal("FALSE", ResultCopyFormatter.FormatCell(false, new ResultCopyOptions { SqlLiterals = true }));
        Assert.Equal("X'AB01'", ResultCopyFormatter.FormatCell(new byte[] { 0xAB, 0x01 }, new ResultCopyOptions { SqlLiterals = true }));
    }
}
