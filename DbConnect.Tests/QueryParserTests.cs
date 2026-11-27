using DbConnect.Services;

namespace DbConnect.Tests;

public class QueryParserTests
{
    [Fact]
    public void ParseAliasMap_MapsFromAndJoinAliasesCaseInsensitively()
    {
        var aliases = QueryParser.ParseAliasMap("""
            select c.id, o.id
            from Customers as c
            inner join Orders o on o.CustomerId = c.Id
            left join OrderItems AS oi on oi.OrderId = o.Id
            """);

        Assert.Equal("Customers", aliases["c"]);
        Assert.Equal("Orders", aliases["O"]);
        Assert.Equal("OrderItems", aliases["oi"]);
    }

    [Theory]
    [InlineData("select * from Customers where c.Id = 1")]
    [InlineData("select * from Customers order by Name")]
    [InlineData("select * from Customers group by Region")]
    public void ParseAliasMap_DoesNotRegisterSqlKeywordsAsAliases(string sql)
    {
        Assert.Empty(QueryParser.ParseAliasMap(sql));
    }
}
