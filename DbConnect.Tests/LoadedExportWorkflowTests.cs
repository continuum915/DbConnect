using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public sealed class LoadedExportWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-loaded-{Guid.NewGuid():N}");
    public LoadedExportWorkflowTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothCommandsHandleZeroRowsAndPickerCancellationWithoutDatabaseWork(bool table, bool cancel)
    {
        var path = Path.Combine(_directory, "empty.csv");
        var picker = new Picker(cancel ? null : path);
        var provider = new TablePagingProvider(CreatePage([]));
        ExportOperationViewModel state;
        if (table)
        {
            var tab = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(true));
            await tab.RefreshInternalAsync(true);
            tab.ExportPathPicker = picker;
            var calls = provider.PageLoads;
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(calls, provider.PageLoads);
            state = tab.ExportState;
        }
        else
        {
            var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { CreateSession(provider) }) { ExportPathPicker = picker };
            tab.SetExecutionResults([CreatePage([]).Result]);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(0, provider.PageLoads);
            Assert.Equal(0, provider.QueryExecutions);
            state = tab.ExportState;
        }
        Assert.Equal(cancel ? ExportStatus.Canceled : ExportStatus.Completed, state.Outcome!.Status);
        Assert.Equal(0, state.Outcome.RowsWritten);
        if (cancel) Assert.Empty(Directory.GetFiles(_directory));
        else Assert.Equal("Id,Name\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task QueryCommandExportsFilteredSortedReorderedUnsavedValuesWithoutDatabaseRerun()
    {
        var dbPath = Path.Combine(_directory, "query.sqlite");
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "query-export",
            Provider = "SQLite",
            ConnectionString = $"Data Source={dbPath}"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        var session = await tab.GetOrOpenDbSessionAsync();
        try
        {
            await session.ExecuteQueryAsync("CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO items VALUES (1,'keep one'),(2,'drop'),(3,'keep three');");
            tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT * FROM items")]);
            await tab.ResultEditor.PendingLoad;
            Assert.True(tab.ResultEditor.IsEditable);
            tab.ResultFilterText = "keep";
            tab.ResultSortColumn = "id";
            tab.ResultSortDescending = true;
            var edited = tab.ResultEditor.RowFor(tab.DisplayResultRows[0])!;
            Assert.Null(tab.ResultEditor.SetCellText(edited, 1, "unsaved,NULL"));
            tab.CaptureExportColumnOrder = () => [1, 0];
            var output = Path.Combine(_directory, "query.csv");
            tab.ExportPathPicker = new Picker(output);
            await session.DisposeAsync(); // Export must use captured loaded values, even with no usable reader.
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal("value,id\r\n\"unsaved,NULL\",3\r\nkeep one,1\r\n", await File.ReadAllTextAsync(output));
        }
        finally
        {
            await session.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private sealed class Picker(string? path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult(path);
    }
}
