using System;
using System.Globalization;
using System.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-07 — Culture-invariant value conversion and NULL/empty-string handling.
/// Numeric parse tests run under several thread cultures (including comma-decimal locales)
/// to prove parsing never depends on <see cref="CultureInfo.CurrentCulture"/>.
/// </summary>
public class CellValueConverterTests : IDisposable
{
    private readonly CultureInfo _originalCulture = Thread.CurrentThread.CurrentCulture;

    public void Dispose() => Thread.CurrentThread.CurrentCulture = _originalCulture;

    private static void WithCulture(string cultureName, Action action)
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            action();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")] // comma decimal separator, dot thousands separator
    [InlineData("fr-FR")] // comma decimal separator
    [InlineData("ar-SA")] // non-Latin digit shaping in some environments
    public void Parse_DotDecimal_NeverCorruptsUnderAnyCulture(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal(1.5m, CellValueConverter.Parse("1.5", ColumnDataType.Decimal));
            Assert.Equal(1.5, CellValueConverter.Parse("1.5", ColumnDataType.Real));
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Parse_CommaDecimal_IsTreatedAsDecimalSeparatorUnderAnyCulture(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal(1.5m, CellValueConverter.Parse("1,5", ColumnDataType.Decimal));
            Assert.Equal(1.5, CellValueConverter.Parse("1,5", ColumnDataType.Real));
        });
    }

    [Fact]
    public void Parse_ThousandsGroupedNumber_StripsGroupingRatherThanCorrupting()
    {
        Assert.Equal(1234.56m, CellValueConverter.Parse("1,234.56", ColumnDataType.Decimal));
        Assert.Equal(1234567L, CellValueConverter.Parse("1,234,567", ColumnDataType.Integer));
    }

    [Fact]
    public void Parse_Integer_RoundTripsExactly()
    {
        Assert.Equal(42L, CellValueConverter.Parse("42", ColumnDataType.Integer));
        Assert.Equal("42", CellValueConverter.FormatForEditor(42L, ColumnDataType.Integer));
    }

    [Fact]
    public void Parse_Boolean_AcceptsTrueFalseAndOneZero()
    {
        Assert.Equal(true, CellValueConverter.Parse("true", ColumnDataType.Boolean));
        Assert.Equal(false, CellValueConverter.Parse("false", ColumnDataType.Boolean));
        Assert.Equal(true, CellValueConverter.Parse("1", ColumnDataType.Boolean));
        Assert.Equal(false, CellValueConverter.Parse("0", ColumnDataType.Boolean));
    }

    [Fact]
    public void Parse_Boolean_RoundTripsThroughFormatForEditor()
    {
        var parsed = CellValueConverter.Parse("true", ColumnDataType.Boolean);
        var formatted = CellValueConverter.FormatForEditor(parsed, ColumnDataType.Boolean);
        Assert.Equal(parsed, CellValueConverter.Parse(formatted, ColumnDataType.Boolean));
    }

    [Fact]
    public void Parse_Date_RoundTripsExactly()
    {
        var parsed = (DateTime)CellValueConverter.Parse("2026-09-27", ColumnDataType.Date)!;
        Assert.Equal(new DateTime(2026, 9, 27), parsed);

        var formatted = CellValueConverter.FormatForEditor(parsed, ColumnDataType.Date);
        Assert.Equal("2026-09-27", formatted);
        Assert.Equal(parsed, CellValueConverter.Parse(formatted, ColumnDataType.Date));
    }

    [Fact]
    public void Parse_DateTime_RoundTripsExactly()
    {
        var parsed = (DateTime)CellValueConverter.Parse("2026-09-27 14:30:05", ColumnDataType.DateTime)!;
        Assert.Equal(new DateTime(2026, 9, 27, 14, 30, 5), parsed);

        var formatted = CellValueConverter.FormatForEditor(parsed, ColumnDataType.DateTime);
        Assert.Equal(parsed, CellValueConverter.Parse(formatted, ColumnDataType.DateTime));
    }

    [Fact]
    public void Parse_Decimal_RoundTripsExactlyWithFourDecimalPlaces()
    {
        var parsed = (decimal)CellValueConverter.Parse("1234567890.1234", ColumnDataType.Decimal)!;
        Assert.Equal(1234567890.1234m, parsed);

        var formatted = CellValueConverter.FormatForEditor(parsed, ColumnDataType.Decimal);
        Assert.Equal(parsed, CellValueConverter.Parse(formatted, ColumnDataType.Decimal));
    }

    [Fact]
    public void Parse_TextColumn_EmptyStringIsNotNull()
    {
        var value = CellValueConverter.Parse("", ColumnDataType.Text);
        Assert.NotNull(value);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void Parse_TextColumn_NullTextArgumentYieldsEmptyStringNotNull()
    {
        // The NULL toggle is what produces an actual null; a Text column parsing null editor
        // text (never happens via the toggle, but should never silently become NULL either).
        var value = CellValueConverter.Parse(null, ColumnDataType.Text);
        Assert.Equal(string.Empty, value);
    }

    [Theory]
    [InlineData(ColumnDataType.Integer)]
    [InlineData(ColumnDataType.Real)]
    [InlineData(ColumnDataType.Decimal)]
    [InlineData(ColumnDataType.Boolean)]
    [InlineData(ColumnDataType.Date)]
    [InlineData(ColumnDataType.DateTime)]
    public void Parse_NonTextColumn_EmptyTextThrowsRatherThanSilentlyBecomingNull(ColumnDataType type)
    {
        Assert.Throws<FormatException>(() => CellValueConverter.Parse("", type));
    }

    [Fact]
    public void Parse_InvalidNumericText_Throws()
    {
        Assert.Throws<FormatException>(() => CellValueConverter.Parse("not-a-number", ColumnDataType.Decimal));
    }

    [Fact]
    public void FormatForEditor_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CellValueConverter.FormatForEditor(null, ColumnDataType.Integer));
        Assert.Equal(string.Empty, CellValueConverter.FormatForEditor(null, ColumnDataType.Text));
    }

    [Fact]
    public void Parse_UnknownColumnType_TreatedAsFreeTextRatherThanThrowingOnEmpty()
    {
        // SQLite/DuckDB columns with no type affinity resolve to Unknown; an empty edit there
        // should behave like a Text column, not throw.
        Assert.Equal(string.Empty, CellValueConverter.Parse("", ColumnDataType.Unknown));
        Assert.Equal("hi", CellValueConverter.Parse("hi", ColumnDataType.Unknown));
    }

    [Fact]
    public void DefaultNonNullValue_MatchesExpectedTypePerColumn()
    {
        Assert.Equal(0L, CellValueConverter.DefaultNonNullValue(ColumnDataType.Integer));
        Assert.Equal(0.0, CellValueConverter.DefaultNonNullValue(ColumnDataType.Real));
        Assert.Equal(0m, CellValueConverter.DefaultNonNullValue(ColumnDataType.Decimal));
        Assert.Equal(false, CellValueConverter.DefaultNonNullValue(ColumnDataType.Boolean));
        Assert.Equal(string.Empty, CellValueConverter.DefaultNonNullValue(ColumnDataType.Text));
    }

    [Theory]
    [InlineData(ColumnDataType.Integer)]
    [InlineData(ColumnDataType.Real)]
    [InlineData(ColumnDataType.Decimal)]
    [InlineData(ColumnDataType.Boolean)]
    [InlineData(ColumnDataType.Date)]
    [InlineData(ColumnDataType.DateTime)]
    [InlineData(ColumnDataType.Guid)]
    public void DefaultNonNullValue_RoundTripsThroughFormatAndParseWithoutThrowing(ColumnDataType type)
    {
        // The NULL toggle writes this value when unchecked; it must always be a valid value for
        // the column's type (never a bare "" that would fail as a SQL parameter of that type).
        var value = CellValueConverter.DefaultNonNullValue(type);
        var formatted = CellValueConverter.FormatForEditor(value, type);
        var reparsed = CellValueConverter.Parse(formatted, type);
        Assert.NotNull(reparsed);
    }

    [Fact]
    public void DefaultNonNullValue_NeverReturnsNull()
    {
        foreach (ColumnDataType type in System.Enum.GetValues<ColumnDataType>())
            Assert.NotNull(CellValueConverter.DefaultNonNullValue(type));
    }

    // RAIN-P2-08 — Microsoft.Data.Sqlite hands back a column's runtime storage class, not its
    // declared type: verified empirically, a DATE/DATETIME column reads as a raw string, BOOLEAN
    // as a long (1/0), DECIMAL as a double. Editors that pattern-match on the expected CLR type
    // (DatePicker/TimePicker/tri-state CheckBox) need this to show a populated cell correctly
    // instead of blank/unchecked.
    [Fact]
    public void Coerce_SqliteRawBooleanLong_BecomesClrBool()
    {
        Assert.Equal(true, CellValueConverter.Coerce(1L, ColumnDataType.Boolean));
        Assert.Equal(false, CellValueConverter.Coerce(0L, ColumnDataType.Boolean));
    }

    [Fact]
    public void Coerce_SqliteRawDateString_BecomesClrDateTime()
    {
        Assert.Equal(new DateTime(2020, 1, 1), CellValueConverter.Coerce("2020-01-01", ColumnDataType.Date));
    }

    [Fact]
    public void Coerce_SqliteRawDateTimeString_BecomesClrDateTime()
    {
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), CellValueConverter.Coerce("2026-09-27 10:00:00", ColumnDataType.DateTime));
    }

    [Fact]
    public void Coerce_SqliteRawDecimalDouble_BecomesClrDecimal()
    {
        Assert.Equal(9.99m, CellValueConverter.Coerce(9.99, ColumnDataType.Decimal));
    }

    [Fact]
    public void Coerce_AlreadyCanonicalValue_IsReturnedUnchanged()
    {
        var date = new DateTime(2020, 1, 1);
        Assert.Equal(date, CellValueConverter.Coerce(date, ColumnDataType.Date));
        Assert.Equal(true, CellValueConverter.Coerce(true, ColumnDataType.Boolean));
        Assert.Equal(1.5m, CellValueConverter.Coerce(1.5m, ColumnDataType.Decimal));
    }

    [Fact]
    public void Coerce_Null_StaysNull()
    {
        Assert.Null(CellValueConverter.Coerce(null, ColumnDataType.Date));
        Assert.Null(CellValueConverter.Coerce(null, ColumnDataType.Boolean));
    }

    // RAIN-P2-09 — JSON columns get validated on parse so a cell edit can never save malformed
    // JSON; the original text is preserved rather than re-serialized.
    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("\"just a string\"")]
    [InlineData("  { \"a\" : 1 }  ")]
    public void Parse_JsonColumn_ValidJsonRoundTripsUnchanged(string text)
    {
        Assert.Equal(text, CellValueConverter.Parse(text, ColumnDataType.Json));
    }

    [Theory]
    [InlineData("{not valid json")]
    [InlineData("{\"a\": }")]
    [InlineData("undefined")]
    public void Parse_JsonColumn_MalformedJsonThrows(string text)
    {
        Assert.Throws<FormatException>(() => CellValueConverter.Parse(text, ColumnDataType.Json));
    }

    [Fact]
    public void Parse_JsonColumn_EmptyTextThrowsRatherThanSilentlyBecomingNull()
    {
        Assert.Throws<FormatException>(() => CellValueConverter.Parse("", ColumnDataType.Json));
    }

    [Fact]
    public void DefaultNonNullValue_Json_IsValidJson()
    {
        var value = (string)CellValueConverter.DefaultNonNullValue(ColumnDataType.Json);
        Assert.Equal(value, CellValueConverter.Parse(value, ColumnDataType.Json));
    }
}
