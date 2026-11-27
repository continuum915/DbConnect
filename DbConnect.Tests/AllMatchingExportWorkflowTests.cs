using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public sealed class AllMatchingExportWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-all-{Guid.NewGuid():N}");
    public AllMatchingExportWorkflowTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.Excel)]
    public async Task CommandCapturesAppliedFiltersStableOrderOptionsAndDatabaseValuesBeforePickerReturns(ExportFormat format)
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "all-export",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        });
        var tab = new TableDataTabViewModel("Customers", connection, CreateTableNode(true)) { PageSize = 1, SkipExactCount = true };
        var session = await tab.GetOrOpenDbSessionAsync();
        try
        {
            await session.ExecuteQueryAsync("CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Customers VALUES (1,'B'),(2,'A'),(3,'A'),(4,'drop');");
            tab.RawWhere = "Id < 4";
            tab.AddFilterRow("Name", TableFilterOperator.NotEquals, "drop");
            await tab.ApplyFiltersAsync();
            await tab.SortByColumn("Name");
            await tab.MoveColumnAsync("Name", 0);
            tab.Rows[0].Values["Name"] = "unsaved";
            tab.MarkRowModified(tab.Rows[0]);
            var picker = new PendingPicker();
            tab.ExportPathPicker = picker;
            tab.ExportScope = ExportScope.AllMatchingTableRows;
            tab.ExportOptions.Format = format;
            var operation = tab.ExportCommand.ExecuteAsync(null);
            tab.RawWhere = "Id = 4";
            tab.FilterRows.Clear();
            tab.PresetFilter("Id", TableFilterOperator.Equals, "4");
            tab.ExportOptions.IncludeHeaders = false;
            tab.ExportOptions.Format = ExportFormat.Csv;
            tab.ExportScope = ExportScope.CurrentTablePage;
            var path = Path.Combine(_directory, "captured.csv");
            picker.Complete.SetResult(path);
            await operation;
            await AssertCapturedFileAsync(path, format);
            Assert.Equal(format, tab.ExportState.Outcome!.Format);
            Assert.Equal(ExportScope.AllMatchingTableRows, tab.ExportState.Outcome!.Scope);
            Assert.Equal(3, tab.ExportState.Outcome.RowsWritten);
            Assert.True(tab.HasDirtyRows);
            Assert.Equal("unsaved", tab.Rows[0].Values["Name"]);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    private static async Task AssertCapturedFileAsync(string path, ExportFormat format)
    {
        if (format == ExportFormat.Excel)
        {
            using var input = File.OpenRead(path);
            using var workbook = new ClosedXML.Excel.XLWorkbook(input);
            var sheet = workbook.Worksheet(1);
            Assert.Equal("Name", sheet.Cell(1, 1).GetString());
            Assert.Equal("Id", sheet.Cell(1, 2).GetString());
            Assert.Equal([2d, 3d, 1d], Enumerable.Range(2, 3).Select(row => sheet.Cell(row, 2).GetDouble()));
            Assert.Equal(["A", "A", "B"], Enumerable.Range(2, 3).Select(row => sheet.Cell(row, 1).GetString()));
            return;
        }
        var text = await File.ReadAllTextAsync(path);
        if (format == ExportFormat.Csv) Assert.Equal("Name,Id\r\nA,2\r\nA,3\r\nB,1\r\n", text);
        else if (format == ExportFormat.Markdown) Assert.Equal("| Name | Id |\n| --- | --- |\n| A | 2 |\n| A | 3 |\n| B | 1 |\n", text);
        else
        {
            using var document = System.Text.Json.JsonDocument.Parse(format == ExportFormat.Json ? text : "[" + string.Join(",", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]");
            var rows = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(["Name", "Id"], rows[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal([2L, 3L, 1L], rows.Select(row => row.GetProperty("Id").GetInt64()));
            Assert.Equal(["A", "A", "B"], rows.Select(row => row.GetProperty("Name").GetString()));
        }
    }

    [Theory]
    [InlineData(false, ExportFormat.Csv)]
    [InlineData(false, ExportFormat.Json)]
    [InlineData(false, ExportFormat.JsonLines)]
    [InlineData(false, ExportFormat.Markdown)]
    [InlineData(false, ExportFormat.Excel)]
    [InlineData(true, ExportFormat.Csv)]
    public async Task EmptyDatabaseAndPickerCancellationPublishOnlyOnNormalCompletion(bool cancel, ExportFormat format)
    {
        var provider = new TablePagingProvider(CreatePage([]));
        var tab = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(true));
        await tab.RefreshInternalAsync(true);
        var before = provider.PageLoads;
        var path = Path.Combine(_directory, "empty.csv");
        var picker = new PendingPicker();
        tab.ExportPathPicker = picker;
        tab.ExportScope = ExportScope.AllMatchingTableRows;
        tab.ExportOptions.Format = format;
        var operation = tab.ExportCommand.ExecuteAsync(null);
        picker.Complete.SetResult(cancel ? null : path);
        await operation;
        Assert.Equal(cancel ? ExportStatus.Canceled : ExportStatus.Completed, tab.ExportState.Outcome!.Status);
        Assert.Equal(before + (cancel ? 0 : 1), provider.PageLoads);
        Assert.Equal(0, tab.ExportState.Outcome.RowsWritten);
        if (cancel) Assert.Empty(Directory.GetFiles(_directory));
        else if (format == ExportFormat.Excel)
        {
            using var input = File.OpenRead(path);
            using var workbook = new ClosedXML.Excel.XLWorkbook(input);
            Assert.Equal(1, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
            Assert.Equal("Id", workbook.Worksheet(1).Cell(1, 1).GetString());
        }
        else
        {
            var text = await File.ReadAllTextAsync(path);
            if (format == ExportFormat.Json)
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                Assert.Equal(0, document.RootElement.GetArrayLength());
            }
            else Assert.Equal(format switch
            {
                ExportFormat.Csv => "Id,Name\r\n",
                ExportFormat.Markdown => "| Id | Name |\n| --- | --- |\n",
                _ => ""
            }, text);
        }
        await tab.ReleaseDbSessionAsync();
    }

    private sealed class PendingPicker : IExportPathPicker
    {
        public TaskCompletionSource<string?> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Complete.Task;
    }
}
