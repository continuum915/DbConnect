using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

public sealed class TableFilterEscapeConformanceTests
{
    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task PatternFilters_MatchWildcardsEscapeCharactersAndBackslashesLiterally(string providerName)
    {
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (providerName is not ("SQLite" or "DuckDB") && string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var fixture = providerName is "SQLite" or "DuckDB"
            ? await ProviderConformanceFixture.CreateLocalAsync(providerName)
            : await ProviderConformanceFixture.CreateExternalAsync(providerName, connectionString!);
        const string value = "50%_!\\";
        var insert = await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString,
            $"INSERT INTO {fixture.QualifiedTableName} ({fixture.Quote("id")}, {fixture.Quote("name")}, {fixture.Quote("amount")}) " +
            $"VALUES (3, {fixture.ParameterPlaceholder(0)}, 30);",
            new QueryExecutionOptions { Parameters = [value] });
        Assert.False(insert.HasError, insert.ErrorMessage);
        var decoys = await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString,
            $"INSERT INTO {fixture.QualifiedTableName} ({fixture.Quote("id")}, {fixture.Quote("name")}, {fixture.Quote("amount")}) " +
            $"VALUES (4, {fixture.ParameterPlaceholder(0)}, 40), (5, {fixture.ParameterPlaceholder(1)}, 50);",
            new QueryExecutionOptions { Parameters = ["50otherx!\\", "50%_\\"] });
        Assert.False(decoys.HasError, decoys.ErrorMessage);

        foreach (var operation in new[] { TableFilterOperator.Contains, TableFilterOperator.StartsWith, TableFilterOperator.EndsWith })
        {
            var spec = new TableQuerySpec([new TableSortKey("id")], 0, 10)
            {
                Filters = [new TableFilter("name", operation, value)]
            };
            var page = await fixture.Provider.LoadTablePageAsync(fixture.ConnectionString, fixture.TableName, spec);
            Assert.False(page.Result.HasError, page.Result.ErrorMessage);
            Assert.Equal(value, Assert.Single(page.Result.ObjectRows)[page.Result.IndexOfColumn("name")]);
            Assert.Equal(1L, await fixture.Provider.CountTableRowsAsync(fixture.ConnectionString, fixture.TableName, spec));
        }
    }
}
