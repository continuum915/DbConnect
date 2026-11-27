using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class FullQueryExportRoutingTests : MainWindowTestBase
{
    [Fact]
    public async Task BatchResultSelectionExportsOnlyThatExecutedStatementBeyondGridCap()
    {
        var service = new DatabaseService();
        var vm = CreateMainWindowViewModel(service);
        var connection = service.CreateSession(new ConnectionInfo { Name = "batch", Provider = "SQLite", ConnectionString = "Data Source=:memory:" });
        vm.ActiveSessionOptions.Add(connection);
        var tab = vm.NewQueryTabWithSession(connection);
        tab.QueryText = "CREATE TEMP TABLE items(value INTEGER); WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<1200) INSERT INTO items SELECT v FROM n; SELECT value FROM items; SELECT 99 AS unrelated;";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        var selected = tab.Results.Single(r => r.ColumnInfos.Any(c => c.Name == "value"));
        Assert.True(selected.IsTruncated);
        Assert.Equal(1000, selected.ObjectRows.Count);
        tab.SelectedResult = selected;
        tab.QueryText = "DROP TABLE items";
        var path = Path.Combine(Path.GetTempPath(), $"rain-batch-full-{Guid.NewGuid():N}.csv");
        try
        {
            tab.ExportScope = ExportScope.FullQuery;
            tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
            tab.ExportPathPicker = new Picker(Task.FromResult<string?>(path));
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            Assert.Equal(1200, tab.ExportState.Outcome.RowsWritten);
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("value", lines[0]);
            Assert.Equal("1200", lines[^1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportBlocksRunExplainAndCommitOnItsTabWhileUnrelatedTabRuns()
    {
        var service = new DatabaseService();
        var vm = CreateMainWindowViewModel(service);
        var connection = service.CreateSession(new ConnectionInfo { Name = "tabs", Provider = "SQLite", ConnectionString = "Data Source=:memory:" });
        vm.ActiveSessionOptions.Add(connection);
        var tab = vm.NewQueryTabWithSession(connection);
        var session = await tab.GetOrOpenDbSessionAsync();
        await session.BeginTransactionAsync();
        tab.RefreshTransactionState();
        tab.QueryText = "SELECT 1 AS original";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        var result = tab.CurrentResult;
        var pick = new TaskCompletionSource<string?>();
        tab.ExportScope = ExportScope.FullQuery;
        tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
        tab.ExportPathPicker = new Picker(pick.Task);
        var exporting = tab.ExportCommand.ExecuteAsync(null);
        try
        {
            Assert.True(tab.IsExecuting);
            tab.QueryText = "SELECT 99 AS changed";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            await vm.ExplainQueryCommand.ExecuteAsync(null);
            await tab.CommitCommand.ExecuteAsync(null);
            Assert.Same(result, tab.CurrentResult);
            Assert.Null(tab.ExplainResult);
            Assert.True(session.IsInTransaction);
            var other = vm.NewQueryTabWithSession(connection);
            other.QueryText = "SELECT 2 AS independent";
            await vm.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.Equal(2L, other.CurrentResult!.ObjectRows.Single()[0]);
        }
        finally
        {
            pick.SetResult(null);
            await exporting;
        }
        Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
        Assert.True(session.IsInTransaction);
    }

    private sealed class Picker(Task<string?> result) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => result;
    }
}
