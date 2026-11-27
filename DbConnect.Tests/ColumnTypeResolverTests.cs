using System;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class ColumnTypeResolverTests
{
    [Theory]
    [InlineData(typeof(int), ColumnDataType.Integer)]
    [InlineData(typeof(long), ColumnDataType.Integer)]
    [InlineData(typeof(short), ColumnDataType.Integer)]
    [InlineData(typeof(double), ColumnDataType.Real)]
    [InlineData(typeof(float), ColumnDataType.Real)]
    [InlineData(typeof(decimal), ColumnDataType.Decimal)]
    [InlineData(typeof(bool), ColumnDataType.Boolean)]
    [InlineData(typeof(System.DateTime), ColumnDataType.DateTime)]
    [InlineData(typeof(System.DateTimeOffset), ColumnDataType.DateTime)]
    [InlineData(typeof(System.Guid), ColumnDataType.Guid)]
    [InlineData(typeof(byte[]), ColumnDataType.Blob)]
    [InlineData(typeof(string), ColumnDataType.Text)]
    public void Resolve_TrustsClrTypeWhenPresent(System.Type clrType, ColumnDataType expected)
    {
        var column = new ColumnInfo { Name = "c", ClrType = clrType, ProviderTypeName = "junk" };
        Assert.Equal(expected, ColumnTypeResolver.Resolve(column));
    }

    [Theory]
    [InlineData("INTEGER", ColumnDataType.Integer)]
    [InlineData("TEXT", ColumnDataType.Text)]
    [InlineData("REAL", ColumnDataType.Real)]
    [InlineData("BLOB", ColumnDataType.Blob)]
    [InlineData("BOOLEAN", ColumnDataType.Boolean)]
    [InlineData("DATE", ColumnDataType.Date)]
    [InlineData("DATETIME", ColumnDataType.DateTime)]
    [InlineData("DECIMAL(18,4)", ColumnDataType.Decimal)]
    [InlineData("VARCHAR(255)", ColumnDataType.Text)]
    [InlineData("NUMERIC(10,2)", ColumnDataType.Decimal)]
    [InlineData("ENUM", ColumnDataType.Enum)]
    [InlineData("SET", ColumnDataType.Set)]
    [InlineData("JSON", ColumnDataType.Json)]
    [InlineData("JSONB", ColumnDataType.Json)]
    public void Resolve_FallsBackToProviderTypeNameWhenClrTypeIsAbsentOrDynamic(string providerTypeName, ColumnDataType expected)
    {
        // SQLite/DuckDB frequently report a null or `object` ClrType for dynamically-typed columns.
        var column = new ColumnInfo { Name = "c", ClrType = null, ProviderTypeName = providerTypeName };
        Assert.Equal(expected, ColumnTypeResolver.Resolve(column));

        var dynamicColumn = new ColumnInfo { Name = "c", ClrType = typeof(object), ProviderTypeName = providerTypeName };
        Assert.Equal(expected, ColumnTypeResolver.Resolve(dynamicColumn));
    }

    [Fact]
    public void Resolve_UnknownEverything_ReturnsUnknown()
    {
        var column = new ColumnInfo { Name = "c", ClrType = null, ProviderTypeName = null };
        Assert.Equal(ColumnDataType.Unknown, ColumnTypeResolver.Resolve(column));
    }

    // RAIN-P2-08 — Microsoft.Data.Sqlite reports a column's CLR type from its runtime storage
    // class, not its declared type (verified empirically): a column declared DATE/DATETIME reads
    // back as ClrType=string, BOOLEAN as ClrType=long, DECIMAL as ClrType=double. The declared
    // ProviderTypeName must win in these cases, or the grid picks a text/number editor for a
    // date/boolean column.
    [Theory]
    [InlineData(typeof(string), "DATE", ColumnDataType.Date)]
    [InlineData(typeof(string), "DATETIME", ColumnDataType.DateTime)]
    [InlineData(typeof(long), "BOOLEAN", ColumnDataType.Boolean)]
    [InlineData(typeof(double), "DECIMAL", ColumnDataType.Decimal)]
    [InlineData(typeof(string), "GUID", ColumnDataType.Guid)]
    public void Resolve_SqliteStorageAffinityClrType_DefersToTheDeclaredProviderTypeName(
        Type reportedClrType, string providerTypeName, ColumnDataType expected)
    {
        var column = new ColumnInfo { Name = "c", ClrType = reportedClrType, ProviderTypeName = providerTypeName };
        Assert.Equal(expected, ColumnTypeResolver.Resolve(column));
    }

    [Fact]
    public void Resolve_SqliteStorageAffinityClrTypeWithNoDeclaredTypeName_FallsBackToTheClrTypeGuess()
    {
        var column = new ColumnInfo { Name = "c", ClrType = typeof(string), ProviderTypeName = null };
        Assert.Equal(ColumnDataType.Text, ColumnTypeResolver.Resolve(column));
    }

    [Fact]
    public void Resolve_SpecificClrType_IsTrustedEvenWhenProviderTypeNameDisagrees()
    {
        // MySqlConnector reports ClrType=bool for a genuine TINYINT(1) boolean column; the specific
        // ClrType wins over the raw declared type name here (unlike the SQLite affinity types above).
        var column = new ColumnInfo { Name = "c", ClrType = typeof(bool), ProviderTypeName = "TINYINT" };
        Assert.Equal(ColumnDataType.Boolean, ColumnTypeResolver.Resolve(column));
    }

    // RAIN-P2-09 — MySqlConnector reports ClrType=string for ENUM/SET/JSON columns (the same
    // ambiguous storage-affinity type SQLite reports), so the declared provider type name must win.
    [Theory]
    [InlineData("ENUM", ColumnDataType.Enum)]
    [InlineData("SET", ColumnDataType.Set)]
    [InlineData("JSON", ColumnDataType.Json)]
    public void Resolve_MySqlStringClrTypeWithEnumSetJsonProviderName_DefersToTheDeclaredProviderTypeName(
        string providerTypeName, ColumnDataType expected)
    {
        var column = new ColumnInfo { Name = "c", ClrType = typeof(string), ProviderTypeName = providerTypeName };
        Assert.Equal(expected, ColumnTypeResolver.Resolve(column));
    }

    [Fact]
    public void Resolve_GenuineIntegerColumn_StillClassifiesAsIntegerDespiteAffinityCheck()
    {
        // A real bigint column reporting ClrType=long (one of the "ambiguous" affinity types)
        // must still resolve correctly when its declared type name also says Integer.
        var column = new ColumnInfo { Name = "c", ClrType = typeof(long), ProviderTypeName = "BIGINT" };
        Assert.Equal(ColumnDataType.Integer, ColumnTypeResolver.Resolve(column));
    }
}
