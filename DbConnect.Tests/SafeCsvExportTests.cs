using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class SafeCsvExportTests
{
    [Theory]
    [InlineData(CsvDelimiter.Comma, ',')]
    [InlineData(CsvDelimiter.Tab, '\t')]
    [InlineData(CsvDelimiter.Pipe, '|')]
    [InlineData(CsvDelimiter.Semicolon, ';')]
    public async Task CsvUsesInvariantRawRepresentationsAndSelectedDelimiter(CsvDelimiter delimiter, char separator)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-{Guid.NewGuid():N}.csv");
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
            var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
                new LoadedExportSource(Enumerable.Range(0, 6).Select(i => new ExportColumn("value", i)),
                    [[12.50m, true, new DateTimeOffset(2026, 10, 18, 10, 20, 30, TimeSpan.FromHours(-4)), new byte[] { 0, 255 }, DBNull.Value, $"x{separator}y"]]),
                new ExportOptions { Delimiter = delimiter, IncludeHeaders = false, NullRepresentation = NullRepresentation.NullLiteral }, ExportScope.CurrentTablePage);
            Assert.Equal(ExportStatus.Completed, outcome.Status);
            const string number = "12.50";
            Assert.Equal(string.Join(separator, number, "true", "2026-10-18T10:20:30.0000000-04:00", "AP8=", "NULL", $"\"x{separator}y\"") + "\r\n", await File.ReadAllTextAsync(path));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; File.Delete(path); }
    }

    [Fact]
    public async Task CsvPreservesRawNullEmptyAndLiteralNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-{Guid.NewGuid():N}.csv");
        try
        {
            var exporter = new ResultExporter(new Picker(path));
            await exporter.ExportAsync(new LoadedExportSource([new("a", 0), new("a", 1), new("c", 2), new("d", 3)],
                [[null, "", "NULL", "a,\"b\"\r\nc"]]),
                new ExportOptions { NullRepresentation = NullRepresentation.BackslashN }, ExportScope.DisplayedQueryResults);
            Assert.Equal("a,a,c,d\r\n\\N,,NULL,\"a,\"\"b\"\"\r\nc\"\r\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task QueryCommandCapturesRowsBeforePickerReturns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-{Guid.NewGuid():N}.csv");
        try
        {
            var picker = new GatedPicker();
            var tab = new DbConnect.ViewModels.QueryTabViewModel { ExportPathPicker = picker };
            tab.SetExecutionResults([new QueryResult { ColumnInfos = [new ColumnInfo { Name = "value" }], ObjectRows = [["before"]] }]);
            var task = tab.ExportCommand.ExecuteAsync(null);
            tab.ExportOptions.IncludeHeaders = false;
            tab.SetExecutionResults([new QueryResult { ColumnInfos = [new ColumnInfo { Name = "later" }], ObjectRows = [["after"]] }]);
            picker.Complete.SetResult(path);
            await task;
            Assert.Equal("value\r\nbefore\r\n", await File.ReadAllTextAsync(path));
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
        }
        finally { File.Delete(path); }
    }

    private sealed class GatedPicker : IExportPathPicker
    {
        public TaskCompletionSource<string?> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Complete.Task;
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
