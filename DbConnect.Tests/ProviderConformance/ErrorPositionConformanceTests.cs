using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

public sealed class ErrorPositionConformanceTests
{
    [Fact]
    public void PostgreSql_Character42_BecomesZeroBasedOffset41()
    {
        Assert.Equal(new QueryErrorPosition(Offset: 41), QueryErrorPositionExtractor.PostgreSql(42));
        Assert.Null(QueryErrorPositionExtractor.PostgreSql(0));
    }

    [Fact]
    public void SqlServer_LineNumber_RemainsOneBased()
    {
        Assert.Equal(new QueryErrorPosition(Line: 3), QueryErrorPositionExtractor.SqlServer(3));
        Assert.Null(QueryErrorPositionExtractor.SqlServer(0));
    }

    [Fact]
    public void MySql_LineInMessage_IsExtractedWhenAvailable()
    {
        Assert.Equal(new QueryErrorPosition(Line: 2),
            QueryErrorPositionExtractor.MySql("You have an error in your SQL syntax at line 2"));
        Assert.Null(QueryErrorPositionExtractor.MySql("Access denied"));
    }

    [Fact]
    public async Task Sqlite_SyntaxError_ReportsNearbyTokenFromRealProvider()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var result = await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString, "SELECT FROM missing_table");

        Assert.True(result.HasError);
        Assert.Equal(new QueryErrorPosition(Offset: 7), result.Error?.Position);
    }

    [Theory]
    [InlineData("PostgreSQL", "SELECT 1 FROM WHERE", 14, null)]
    [InlineData("SQL Server", "SELECT 1;\nSELECT FROM missing_table", null, 2)]
    [Trait("Category", "Integration")]
    public async Task ExternalProvider_SyntaxError_ReportsDriverPositionWhenConfigured(
        string providerName, string sql, int? offset, int? line)
    {
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var provider = new DatabaseService().GetProvider(providerName);
        var result = await provider.ExecuteQueryAsync(connectionString, sql);

        Assert.True(result.HasError);
        Assert.Equal(new QueryErrorPosition(offset, line), result.Error?.Position);
    }
}
