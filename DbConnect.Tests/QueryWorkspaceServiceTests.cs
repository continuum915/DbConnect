using System;
using System.IO;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class QueryWorkspaceServiceTests
{
    [Fact]
    public async Task WorkspaceService_PersistsTimeoutHistorySavedQueriesRowCapAndTablePageSize()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"rain-query-workspace-{Guid.NewGuid():N}.json");
        var service = new QueryWorkspaceService(filePath);
        var connection = new ConnectionInfo
        {
            Name = "Local",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        };

        await service.SaveTimeoutSecondsAsync(90);
        await service.SaveResultRowCapAsync(500);
        await service.SaveTablePageSizeAsync(250);
        await service.AddHistoryEntryAsync(connection, new QueryHistoryEntry
        {
            Sql = "SELECT 1;",
            ProviderName = connection.Provider,
            Scope = QueryExecutionScope.FullEditor
        });
        await service.SaveQueryAsync(new SavedQueryDefinition
        {
            Name = "Check",
            Sql = "SELECT 1;",
            ProviderName = connection.Provider
        });

        var reloaded = new QueryWorkspaceService(filePath);
        var timeout = await reloaded.LoadTimeoutSecondsAsync();
        var rowCap = await reloaded.LoadResultRowCapAsync();
        var tablePageSize = await reloaded.LoadTablePageSizeAsync();
        var history = await reloaded.LoadHistoryAsync(connection);
        var saved = await reloaded.LoadSavedQueriesAsync();

        Assert.Equal(90, timeout);
        Assert.Equal(500, rowCap);
        Assert.Equal(250, tablePageSize);
        Assert.Single(history);
        Assert.Equal("SELECT 1;", history[0].Sql);
        var savedQuery = Assert.Single(saved);
        Assert.Equal("Check", savedQuery.Name);

        File.Delete(filePath);
    }

    [Fact]
    public async Task WorkspaceService_PersistsColumnLayoutPerConnectionAndTable()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"rain-query-workspace-{Guid.NewGuid():N}.json");
        var local = new ConnectionInfo { Name = "Local", Provider = "SQLite", ConnectionString = "Data Source=a.db" };
        var other = new ConnectionInfo { Name = "Other", Provider = "SQLite", ConnectionString = "Data Source=b.db" };
        try
        {
            var service = new QueryWorkspaceService(filePath);
            await service.SaveTableColumnLayoutAsync(local, "main.Customers", new TableColumnLayout
            {
                Order = ["Name", "Id", "Email"],
                Hidden = ["Email"],
                FrozenCount = 1
            });

            var reloaded = new QueryWorkspaceService(filePath);
            var layout = await reloaded.LoadTableColumnLayoutAsync(local, "main.Customers");

            Assert.Equal(["Name", "Id", "Email"], layout.Order);
            Assert.Equal(["Email"], layout.Hidden);
            Assert.Equal(1, layout.FrozenCount);
            Assert.Empty((await reloaded.LoadTableColumnLayoutAsync(local, "main.Orders")).Order);
            Assert.Empty((await reloaded.LoadTableColumnLayoutAsync(other, "main.Customers")).Order);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
