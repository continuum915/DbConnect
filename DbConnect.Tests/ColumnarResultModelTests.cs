using System.Threading.Tasks;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>RAIN-207 (ticket 07) — the columnar result shape added alongside the dictionary one.</summary>
public class ColumnarResultModelTests
{
    [Fact]
    public async Task DuplicateColumnNames_AreDistinctEntriesByOrdinal()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";

        var result = await provider.ExecuteQueryAsync(connectionString, "SELECT 1 AS id, 2 AS id;");

        Assert.False(result.HasError);
        Assert.Equal(2, result.ColumnInfos.Count);
        Assert.Equal("id", result.ColumnInfos[0].Name);
        Assert.Equal(0, result.ColumnInfos[0].Ordinal);
        Assert.Equal("id", result.ColumnInfos[1].Name);
        Assert.Equal(1, result.ColumnInfos[1].Ordinal);

        Assert.Single(result.ObjectRows);
        Assert.Equal(2, result.ObjectRows[0].Length);
        Assert.Equal(1L, result.ObjectRows[0][0]);
        Assert.Equal(2L, result.ObjectRows[0][1]);
    }

    [Fact]
    public async Task ColumnInfo_CarriesProviderTypeNameAndClrType()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";

        var result = await provider.ExecuteQueryAsync(connectionString, "SELECT 'hello' AS greeting;");

        Assert.False(result.HasError);
        var column = Assert.Single(result.ColumnInfos);
        Assert.Equal(typeof(string), column.ClrType);
        Assert.NotNull(column.ProviderTypeName);
    }

    [Fact]
    public async Task StatementWithNoResultSet_PopulatesNeitherShape()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";

        var result = await provider.ExecuteQueryAsync(connectionString, "CREATE TABLE t (id INTEGER);");

        Assert.False(result.HasError);
        Assert.Empty(result.ColumnInfos);
        Assert.Empty(result.ObjectRows);
    }

    [Fact]
    public async Task MultipleResultSets_EachGetsItsOwnColumnarShape()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";

        var results = new System.Collections.Generic.List<DbConnect.Models.QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, "SELECT 1 AS a; SELECT 2 AS b, 3 AS c;"))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Single(results[0].ColumnInfos);
        Assert.Equal("a", results[0].ColumnInfos[0].Name);
        Assert.Equal(2, results[1].ColumnInfos.Count);
        Assert.Equal("b", results[1].ColumnInfos[0].Name);
        Assert.Equal("c", results[1].ColumnInfos[1].Name);
    }
}
