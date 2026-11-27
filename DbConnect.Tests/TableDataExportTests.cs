using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public sealed class TableDataExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-export-{Guid.NewGuid():N}");

    public TableDataExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Export_Excel_WritesReadableWorkbook()
    {
        var (viewModel, picker) = await LoadTableTabAsync(ExportFormat.Excel);

        await viewModel.ExportCommand.ExecuteAsync(null);

        using var workbook = new XLWorkbook(picker.LastPath!);
        var ws = workbook.Worksheet(1);
        Assert.Equal("Id", ws.Cell(1, 1).GetString());
        Assert.Equal("Name", ws.Cell(1, 2).GetString());
        Assert.Equal("1", ws.Cell(2, 1).GetString());
        Assert.Equal("Name 2", ws.Cell(3, 2).GetString());
    }

    [Fact]
    public async Task Export_JsonLines_WritesOneObjectPerLine()
    {
        var (viewModel, picker) = await LoadTableTabAsync(ExportFormat.JsonLines);

        await viewModel.ExportCommand.ExecuteAsync(null);

        var lines = await File.ReadAllLinesAsync(picker.LastPath!);
        Assert.Equal(2, lines.Length);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, second.RootElement.GetProperty("Id").GetInt64());
        Assert.Equal("Name 2", second.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Export_Markdown_WritesTable()
    {
        var (viewModel, picker) = await LoadTableTabAsync(ExportFormat.Markdown);

        await viewModel.ExportCommand.ExecuteAsync(null);

        var lines = await File.ReadAllLinesAsync(picker.LastPath!);
        Assert.Equal(["| Id | Name |", "| --- | --- |", "| 1 | Name 1 |", "| 2 | Name 2 |"], lines);
    }

    [Theory]
    [InlineData(ExportFormat.Csv, "export.csv", "*.csv")]
    [InlineData(ExportFormat.Json, "export.json", "*.json")]
    [InlineData(ExportFormat.Excel, "export.xlsx", "*.xlsx")]
    [InlineData(ExportFormat.JsonLines, "export.jsonl", "*.jsonl")]
    [InlineData(ExportFormat.Markdown, "export.md", "*.md")]
    public async Task Export_SuggestsFileNameAndFilterMatchingFormat(ExportFormat format, string fileName, string pattern)
    {
        var (viewModel, picker) = await LoadTableTabAsync(format);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(fileName, picker.LastSuggestedFileName);
        Assert.Equal(pattern, $"*.{picker.LastFileType!.Extension}");
        Assert.EndsWith(fileName, viewModel.ExportState.Outcome!.FinalPath);
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Excel)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Markdown)]
    public async Task Export_SavedAllPagesWritesEveryFormat(ExportFormat format)
    {
        var (viewModel, picker) = await LoadTableTabAsync(format, allPages: true);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(ExportScope.AllMatchingTableRows, viewModel.ExportScope);
        Assert.Equal(ExportStatus.Completed, viewModel.ExportState.Outcome!.Status);
        Assert.Equal(3, viewModel.ExportState.Outcome.RowsWritten);
        Assert.Equal(ExportScope.AllMatchingTableRows, viewModel.ExportState.Outcome.Scope);
        Assert.Equal(format, viewModel.ExportState.Outcome.Format);
        var text = await ReadAsTextAsync(picker.LastPath!, format);
        switch (format)
        {
            case ExportFormat.Csv:
                Assert.Equal("Id,Name\r\n1,Name 1\r\n2,Name 2\r\n3,Name 3\r\n", text);
                break;
            case ExportFormat.Excel:
                Assert.Equal("Id\tName\n1\tName 1\n2\tName 2\n3\tName 3", text);
                using (var workbook = new XLWorkbook(picker.LastPath!))
                    Assert.Equal(XLDataType.Number, workbook.Worksheet(1).Cell(4, 1).DataType);
                break;
            case ExportFormat.Markdown:
                Assert.Equal("| Id | Name |\n| --- | --- |\n| 1 | Name 1 |\n| 2 | Name 2 |\n| 3 | Name 3 |", text.Replace("\r\n", "\n").TrimEnd());
                break;
            default:
                using (var json = JsonDocument.Parse(format == ExportFormat.Json ? text : "[" + string.Join(",", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]"))
                {
                    var rows = json.RootElement.EnumerateArray().ToArray();
                    Assert.Equal(3, rows.Length);
                    Assert.Equal(["Id", "Name"], rows[0].EnumerateObject().Select(p => p.Name));
                    Assert.Equal(3L, rows[2].GetProperty("Id").GetInt64());
                    Assert.Equal("Name 3", rows[2].GetProperty("Name").GetString());
                }
                break;
        }
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Excel)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Markdown)]
    public async Task Export_MatchesQueryTabOutputForSameRows(ExportFormat format)
    {
        var (tableTab, tablePicker) = await LoadTableTabAsync(format);
        var queryTab = new QueryTabViewModel();
        queryTab.SetExecutionResults([CreatePage([1, 2]).Result]);
        var queryPicker = new RecordingPathPicker(_directory);
        queryTab.ExportPathPicker = queryPicker;
        queryTab.ExportOptions = new ExportOptions { Format = format };

        await tableTab.ExportCommand.ExecuteAsync(null);
        await queryTab.ExportCommand.ExecuteAsync(null);

        Assert.Equal(await ReadAsTextAsync(queryPicker.LastPath!, format), await ReadAsTextAsync(tablePicker.LastPath!, format));
    }

    [Fact]
    public async Task TableCommandCapturesUnsavedEditsColumnOrderAndOptionsBeforePickerReturns()
    {
        var (tab, _) = await LoadTableTabAsync(ExportFormat.Csv);
        await tab.MoveColumnAsync("Name", 0);
        tab.Rows[0].Values["Name"] = "unsaved,NULL";
        var picker = new PendingPicker();
        tab.ExportPathPicker = picker;
        var task = tab.ExportCommand.ExecuteAsync(null);
        tab.Rows[0].Values["Name"] = "later";
        tab.ExportOptions.IncludeHeaders = false;
        var path = Path.Combine(_directory, "captured.csv");
        picker.Result.SetResult(path);
        await task;
        Assert.Equal("Name,Id\r\n\"unsaved,NULL\",1\r\nName 2,2\r\n", await File.ReadAllTextAsync(path));
        Assert.Equal(ExportScope.CurrentTablePage, tab.ExportState.Outcome!.Scope);
    }

    [Fact]
    public async Task TablePickerCancellationCreatesNoFileAndReportsCanceled()
    {
        var (tab, _) = await LoadTableTabAsync(ExportFormat.Csv);
        var picker = new PendingPicker();
        tab.ExportPathPicker = picker;
        picker.Result.SetResult(null);
        await tab.ExportCommand.ExecuteAsync(null);
        Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.Excel)]
    public async Task AllMatchingPreservesRawBooleanDecimalAndNullValues(ExportFormat format)
    {
        var page = CreateTypedPage();
        page.Result.ObjectRows[0][1] = null;
        var tab = new TableDataTabViewModel("Customers", CreateSession(new TablePagingProvider(page)), CreateTableNode(true));
        try
        {
            await tab.RefreshInternalAsync(true);
            var picker = new RecordingPathPicker(_directory);
            tab.ExportPathPicker = picker;
            tab.ExportOptions = new ExportOptions { Format = format, AllPages = true, NullRepresentation = NullRepresentation.NullLiteral };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            if (format == ExportFormat.Excel)
            {
                using var workbook = new XLWorkbook(picker.LastPath!);
                var sheet = workbook.Worksheet(1);
                Assert.True(sheet.Cell(2, 2).IsEmpty());
                Assert.True(sheet.Cell(2, 3).GetBoolean());
                Assert.Equal(9.99d, sheet.Cell(2, 6).GetDouble());
            }
            else if (format is ExportFormat.Json or ExportFormat.JsonLines)
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(picker.LastPath!));
                var row = format == ExportFormat.Json ? document.RootElement[0] : document.RootElement;
                Assert.Equal(JsonValueKind.Null, row.GetProperty("Name").ValueKind);
                Assert.True(row.GetProperty("IsActive").GetBoolean());
                Assert.Equal(9.99m, row.GetProperty("Price").GetDecimal());
            }
            else
            {
                var text = await File.ReadAllTextAsync(picker.LastPath!);
                Assert.Contains("NULL", text);
                Assert.Contains("true", text);
                Assert.Contains("9.99", text);
                Assert.Contains("2026-09-27T10:00:00.0000000", text);
            }
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    private sealed class PendingPicker : IExportPathPicker
    {
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Result.Task;
    }
    /// <summary>The file's content as text; a workbook is read back cell by cell, since its zip bytes carry timestamps.</summary>
    private static async Task<string> ReadAsTextAsync(string path, ExportFormat format)
    {
        if (format != ExportFormat.Excel)
            return await File.ReadAllTextAsync(path);

        using var workbook = new XLWorkbook(path);
        return string.Join("\n", workbook.Worksheet(1).RowsUsed()
            .Select(row => string.Join("\t", row.CellsUsed().Select(cell => cell.GetString()))));
    }

    private async Task<(TableDataTabViewModel ViewModel, RecordingPathPicker Picker)> LoadTableTabAsync(
        ExportFormat format, bool allPages = false)
    {
        var provider = new TablePagingProvider(CreatePage([1, 2], hasNextPage: true), CreatePage([3]));
        var viewModel = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var picker = new RecordingPathPicker(_directory);
        viewModel.ExportPathPicker = picker;
        viewModel.ExportOptions = new ExportOptions { Format = format, AllPages = allPages };
        return (viewModel, picker);
    }

    /// <summary>File-dialog stand-in: accepts the suggested name inside the test run's directory.</summary>
    private sealed class RecordingPathPicker(string directory) : IExportPathPicker
    {
        public string? LastPath { get; private set; }
        public ExportFileType? LastFileType { get; private set; }
        public string? LastSuggestedFileName { get; private set; }

        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType)
        {
            LastSuggestedFileName = suggestedFileName;
            LastFileType = fileType;
            LastPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{suggestedFileName}");
            return Task.FromResult<string?>(LastPath);
        }
    }
}
