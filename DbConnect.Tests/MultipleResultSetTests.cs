using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class MultipleResultSetTests
{
    [Fact]
    public async Task SqliteProvider_MultipleStatements_ReturnsMultipleResults()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";
        var sql = "SELECT 1 AS a; SELECT 2 AS b";

        var results = new List<QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, sql))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Equal("Result 1", results[0].TabLabel);
        Assert.Equal("Result 2", results[1].TabLabel);
    }

    [Fact]
    public async Task SqliteProvider_SingleStatement_ReturnsResultsLabel()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";
        var sql = "SELECT 42 AS answer";

        var results = new List<QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, sql))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal("Results", results[0].TabLabel);
    }

    [Fact]
    public async Task SqliteProvider_NonQueryStatement_ReturnsMessageResult()
    {
        var provider = new SqliteProvider();
        var connectionString = "Data Source=:memory:";
        var sql = "CREATE TABLE IF NOT EXISTS t (id INTEGER); SELECT * FROM t";

        var results = new List<QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, sql))
            results.Add(r);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task DuckDbProvider_MultipleStatements_ReturnsMultipleResults()
    {
        var provider = new DuckDbProvider();
        var connectionString = "Data Source=:memory:";
        var sql = "SELECT 1 AS a; SELECT 2 AS b";

        var results = new List<QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, sql))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Equal("Result 1", results[0].TabLabel);
        Assert.Equal("Result 2", results[1].TabLabel);
    }

    [Fact]
    public async Task DuckDbProvider_SingleStatement_ReturnsResultsLabel()
    {
        var provider = new DuckDbProvider();
        var connectionString = "Data Source=:memory:";
        var sql = "SELECT 42 AS answer";

        var results = new List<QueryResult>();
        await foreach (var r in provider.ExecuteQueryMultipleAsync(connectionString, sql))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal("Results", results[0].TabLabel);
    }

    [Fact]
    public void QueryResult_DefaultTabLabel_IsResults()
    {
        var r = new QueryResult();
        Assert.Equal("Results", r.TabLabel);
    }

    [Fact]
    public void QueryResult_ResultIndex_DefaultsToZero()
    {
        var r = new QueryResult();
        Assert.Equal(0, r.ResultIndex);
    }
}
