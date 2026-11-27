using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Editing a query result from a table without a primary key, after opting in (Tier 2 §2.9).</summary>
public sealed class GridKeylessConformanceTests : MainWindowTestBase
{
    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    [Trait("Category", "Integration")]
    public async Task KeylessRows_UpdateOneRow_AndRefuseDuplicates(string providerName)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var provider = new DatabaseService().GetProvider(providerName);
        var table = "rain_keyless_" + Guid.NewGuid().ToString("N")[..8];
        await provider.ExecuteNonQueryAsync(cs, $"CREATE TABLE {table} (label varchar(10), qty int)");
        QueryTabViewModel? tab = null;
        try
        {
            await provider.ExecuteNonQueryAsync(cs, $"INSERT INTO {table} VALUES ('x', 1), ('dup', 5), ('dup', 5)");
            var established = await new ConnectionEstablisher(new DatabaseService()).EstablishAsync(
                new ConnectionInfo { Name = "live", Provider = providerName, ConnectionString = cs });
            var vm = CreateMainWindowViewModel(new DatabaseService());
            vm.Tabs.Clear();
            tab = new QueryTabViewModel();
            vm.Tabs.Add(tab);
            vm.SelectedTab = tab;
            vm.ActiveSessionOptions.Add(established.Session);
            tab.SelectedSession = established.Session;
            tab.QueryText = $"SELECT label, qty FROM {table}";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            await tab.ResultEditor.PendingLoad;
            if (!established.Session.Capabilities.ReportsResultColumnSources)
            {
                // MySQL results do not say which table a column came from, so no query result is editable there.
                Assert.Contains("doesn't report which table", tab.ResultEditor.ReadOnlyReason);
                return;
            }
            Assert.True(tab.ResultEditor.OffersAllColumnIdentity, tab.ResultEditor.ReadOnlyReason);

            tab.ResultEditor.AllowAllColumnIdentity = true;
            await tab.ResultEditor.PendingLoad;
            Assert.True(tab.ResultEditor.IsEditable, tab.ResultEditor.ReadOnlyReason);
            var x = tab.ResultEditor.RowFor(tab.DisplayResultRows.First(r => r[0] == "x"))!;
            Assert.Null(tab.ResultEditor.SetCellText(x, 1, "10"));
            await tab.ResultEditor.ExecuteCommitAsync();
            Assert.Equal(10, Convert.ToInt32((await provider.ExecuteQueryAsync(cs, $"SELECT qty FROM {table} WHERE label = 'x'")).ObjectRows[0][0]));

            var dup = tab.ResultEditor.RowFor(tab.DisplayResultRows.First(r => r[0] == "dup"))!;
            Assert.Null(tab.ResultEditor.SetCellText(dup, 1, "6"));
            await tab.ResultEditor.ExecuteCommitAsync();
            Assert.Equal(2, Convert.ToInt32((await provider.ExecuteQueryAsync(cs, $"SELECT count(*) FROM {table} WHERE qty = 5")).ObjectRows[0][0]));
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            await provider.ExecuteNonQueryAsync(cs, $"DROP TABLE {table}");
        }
    }
}
