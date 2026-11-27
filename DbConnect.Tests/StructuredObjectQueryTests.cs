using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class StructuredObjectQueryTests
{
    [Theory]
    [InlineData("SQL Server", "[db.name].[a]]b].[raw.name]")]
    [InlineData("PostgreSQL", "\"db.name\".\"a]b\".\"raw.name\"")]
    [InlineData("MySQL", "`db.name`.`a]b`.`raw.name`")]
    public void CountQuotesCatalogSchemaAndNameIndependently(string providerName, string expected)
    {
        var provider = new DatabaseService().GetProvider(providerName);
        var spec = new TableQuerySpec([], 0, 10)
        {
            Object = new DatabaseObjectIdentity("db.name", "a]b", "raw.name", SchemaNodeType.Table)
        };
        Assert.Equal($"SELECT COUNT(*) FROM {expected};", provider.BuildTableCountQuery("ignored", spec).Sql);
    }

    [Fact]
    public void PageAndCountQuoteRawSegmentsWithoutSplittingDots()
    {
        var provider = new PostgreSqlProvider();
        var identity = new DatabaseObjectIdentity(null, "sales.eu", "order.\" details]", SchemaNodeType.Table);
        var spec = new TableQuerySpec([], 0, 10) { Object = identity };

        Assert.Equal("SELECT * FROM \"sales.eu\".\"order.\"\" details]\" LIMIT 11 OFFSET 0;",
            provider.BuildTablePageQuery("unused", spec).Sql);
        Assert.Equal("SELECT COUNT(*) FROM \"sales.eu\".\"order.\"\" details]\";",
            provider.BuildTableCountQuery("unused", spec).Sql);
    }
}
