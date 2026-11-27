using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Fetching past the row cap on live servers (Tier 2 §2.8).</summary>
public sealed class LargeQueryResultConformanceTests : MainWindowTestBase
{
    private static string Numbers(string provider) => provider switch
    {
        "PostgreSQL" => "SELECT g AS n FROM generate_series(1, 100000) AS g ORDER BY g",
        "MySQL" => "WITH RECURSIVE d(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM d WHERE n < 9) " +
                   "SELECT a.n + 10 * b.n + 100 * c.n + 1000 * e.n + 10000 * f.n + 1 AS n " +
                   "FROM d a, d b, d c, d e, d f ORDER BY n",
        _ => "SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b ORDER BY n"
    };

    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab)> TabOn(string provider, string cs)
    {
        var established = await new ConnectionEstablisher(new DatabaseService()).EstablishAsync(
            new ConnectionInfo { Name = "live", Provider = provider, ConnectionString = cs });
        var vm = CreateMainWindowViewModel(new DatabaseService());
        vm.Tabs.Clear();
        var tab = new QueryTabViewModel();
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        vm.ActiveSessionOptions.Add(established.Session);
        tab.SelectedSession = established.Session;
        tab.ResultRowCap = 5000;
        tab.TimeoutSeconds = 120;
        return (vm, tab);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    [Trait("Category", "Integration")]
    public async Task PagesA100000RowResultToTheEnd_InAManualCommitTransaction(string provider)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(provider);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var (vm, tab) = await TabOn(provider, cs);
        tab.IsManualCommit = true;
        try
        {
            tab.QueryText = Numbers(provider);
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
            Assert.Equal(5000, tab.CurrentResult.ObjectRows.Count);
            Assert.True(tab.Paging.HasMore);

            await tab.Paging.FetchNextCommand.ExecuteAsync(null);
            Assert.Equal(10000, tab.CurrentResult.ObjectRows.Count);
            await tab.Paging.FetchAllCommand.ExecuteAsync(null);

            var values = tab.CurrentResult.ObjectRows.Select(r => Convert.ToInt64(r[0])).ToList();
            Assert.Equal(100000, values.Count);
            Assert.Equal(Enumerable.Range(1, 100000).Select(i => (long)i), values);
            Assert.False(tab.Paging.HasMore);

            // The session is free again: the transaction commits.
            Assert.True(tab.IsInTransaction);
            await tab.CommitCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(tab.IsInTransaction);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_LaterResultSetsAppearAfterThePagedOneIsExhausted()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var (vm, tab) = await TabOn("SQL Server", cs);
        try
        {
            tab.QueryText = Numbers("SQL Server").Replace("TOP (100000)", "TOP (12000)") + "; SELECT 42 AS answer";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.True(tab.Paging.HasMore);
            Assert.Single(tab.Results);

            await tab.Paging.FetchAllCommand.ExecuteAsync(null);
            Assert.Equal(12000, tab.Results[0].ObjectRows.Count);
            Assert.Equal(2, tab.Results.Count);
            Assert.Equal(42, Convert.ToInt32(tab.Results[1].ObjectRows[0][0]));
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }
}
