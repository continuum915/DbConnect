using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// Dialect-specific rendering for the three network providers (ticket 04): paging syntax,
/// text-cast target, and case-insensitive LIKE differ per provider even though they all share
/// <see cref="TableQuerySql"/>.
/// </summary>
public class NetworkProviderTableQuerySqlTests
{
    [Fact]
    public void PostgreSql_BuildTablePageQuery_UsesLimitOffsetAndIlike()
    {
        var provider = new PostgreSqlProvider();
        var spec = new TableQuerySpec([new TableSortKey("name")], PageIndex: 1, PageSize: 10)
        {
            Filters = [new TableFilter("bio", TableFilterOperator.Contains, "eng")]
        };

        var query = provider.BuildTablePageQuery("public.customers", spec);

        Assert.Equal(
            "SELECT * FROM \"public\".\"customers\" WHERE CAST(\"bio\" AS TEXT) ILIKE @p0 ESCAPE '!' ORDER BY \"name\" ASC LIMIT 11 OFFSET 10;",
            query.Sql);
        Assert.Equal(["%eng%"], query.Parameters);
    }

    [Fact]
    public void PostgreSql_BuildTableCountQuery_IgnoresSortAndPaging()
    {
        var provider = new PostgreSqlProvider();
        var spec = new TableQuerySpec([new TableSortKey("name", Descending: true)], PageIndex: 4, PageSize: 10);

        var query = provider.BuildTableCountQuery("customers", spec);

        Assert.Equal("SELECT COUNT(*) FROM \"customers\";", query.Sql);
    }

    [Fact]
    public void SqlServer_BuildTablePageQuery_UsesOffsetFetchAndNvarcharCast()
    {
        var provider = new SqlServerProvider();
        var spec = new TableQuerySpec([new TableSortKey("Name")], PageIndex: 1, PageSize: 10)
        {
            Filters = [new TableFilter("Bio", TableFilterOperator.Contains, "eng")]
        };

        var query = provider.BuildTablePageQuery("dbo.Customers", spec);

        Assert.Equal(
            "SELECT * FROM [dbo].[Customers] WHERE CAST([Bio] AS NVARCHAR(MAX)) LIKE @p0 ESCAPE '!' ORDER BY [Name] ASC OFFSET 10 ROWS FETCH NEXT 11 ROWS ONLY;",
            query.Sql);
        Assert.Equal(["%eng%"], query.Parameters);
    }

    [Fact]
    public void SqlServer_BuildTablePageQuery_NoSort_FallsBackToNoOpOrderBy()
    {
        var provider = new SqlServerProvider();
        var spec = new TableQuerySpec([], PageIndex: 0, PageSize: 5);

        var query = provider.BuildTablePageQuery("Customers", spec);

        Assert.Equal(
            "SELECT * FROM [Customers] ORDER BY (SELECT 1) OFFSET 0 ROWS FETCH NEXT 6 ROWS ONLY;",
            query.Sql);
    }

    [Fact]
    public void SqlServer_BuildTableCountQuery_IgnoresSortAndPaging()
    {
        var provider = new SqlServerProvider();
        var spec = new TableQuerySpec([new TableSortKey("Name", Descending: true)], PageIndex: 4, PageSize: 10);

        var query = provider.BuildTableCountQuery("Customers", spec);

        Assert.Equal("SELECT COUNT(*) FROM [Customers];", query.Sql);
    }

    [Fact]
    public void MySql_BuildTablePageQuery_UsesLimitOffsetAndCharCast()
    {
        var provider = new MySqlProvider();
        var spec = new TableQuerySpec([new TableSortKey("name")], PageIndex: 1, PageSize: 10)
        {
            Filters = [new TableFilter("bio", TableFilterOperator.Contains, "eng")]
        };

        var query = provider.BuildTablePageQuery("customers", spec);

        Assert.Equal(
            "SELECT * FROM `customers` WHERE CAST(`bio` AS CHAR) LIKE @p0 ESCAPE '!' ORDER BY `name` ASC LIMIT 11 OFFSET 10;",
            query.Sql);
        Assert.Equal(["%eng%"], query.Parameters);
    }

    [Fact]
    public void MySql_BuildTableCountQuery_IgnoresSortAndPaging()
    {
        var provider = new MySqlProvider();
        var spec = new TableQuerySpec([new TableSortKey("name", Descending: true)], PageIndex: 4, PageSize: 10);

        var query = provider.BuildTableCountQuery("customers", spec);

        Assert.Equal("SELECT COUNT(*) FROM `customers`;", query.Sql);
    }
}
