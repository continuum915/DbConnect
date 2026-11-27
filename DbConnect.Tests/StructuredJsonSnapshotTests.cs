using System.Text.Json;
using System.Text.Json.Nodes;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class StructuredJsonSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-snapshot-{Guid.NewGuid():N}");
    public StructuredJsonSnapshotTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(0, ExportFormat.Json)]
    [InlineData(0, ExportFormat.JsonLines)]
    [InlineData(1, ExportFormat.Json)]
    [InlineData(1, ExportFormat.JsonLines)]
    [InlineData(2, ExportFormat.Json)]
    [InlineData(2, ExportFormat.JsonLines)]
    public async Task ExportPreservesStructuredValuesCapturedBeforePickerCompletes(int entryPoint, ExportFormat format)
    {
        var node = JsonNode.Parse("{\"items\":[{\"name\":\"original\"},null,true]}")!;
        var array = JsonNode.Parse("[1,{\"nested\":[false,null]}]")!;
        using var document = JsonDocument.Parse("{\"document\":[1,2]}");
        using var elementOwner = JsonDocument.Parse("[\"element\",3]");
        byte[] bytes = [0, 255];
        var result = new QueryResult
        {
            ColumnInfos = new[] { "node", "array", "document", "element", "bytes", "text" }
                .Select((name, ordinal) => new ColumnInfo { Name = name, Ordinal = ordinal }).ToList(),
            ObjectRows = [[node, array, document, elementOwner.RootElement, bytes, "{\"text\":true}"]]
        };
        var picker = new PendingPicker();
        Task<ExportOutcome> export;
        if (entryPoint == 0)
        {
            var source = new LoadedExportSource(result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal)), result.ObjectRows);
            export = new ResultExporter(picker).ExportAsync(source, new() { Format = format }, ExportScope.DisplayedQueryResults);
        }
        else if (entryPoint == 1)
        {
            var tab = new QueryTabViewModel { ExportPathPicker = picker };
            tab.SetExecutionResults([result]);
            tab.ExportOptions.Format = format;
            export = RunCommand(tab.ExportCommand.ExecuteAsync(null), tab.ExportState);
        }
        else
        {
            var provider = new TableDataTestFixtures.TablePagingProvider(new TablePageResult { Result = result });
            var tab = new TableDataTabViewModel("Customers", TableDataTestFixtures.CreateSession(provider), TableDataTestFixtures.CreateTableNode(true));
            await tab.RefreshInternalAsync(true);
            tab.ExportPathPicker = picker;
            tab.ExportOptions.Format = format;
            export = RunCommand(tab.ExportCommand.ExecuteAsync(null), tab.ExportState);
        }
        await picker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(export.IsCompleted);
        node["items"]![0]!["name"] = "changed";
        array[1]!["nested"]!.AsArray().Clear();
        bytes[0] = 42;
        document.Dispose();
        elementOwner.Dispose();
        var path = Path.Combine(_directory, "output");
        picker.Completion.SetResult(path);
        Assert.Equal(ExportStatus.Completed, (await export).Status);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var row = format == ExportFormat.Json ? output.RootElement[0] : output.RootElement;
        Assert.Equal("{\"items\":[{\"name\":\"original\"},null,true]}", row.GetProperty("node").GetRawText());
        Assert.Equal("[1,{\"nested\":[false,null]}]", row.GetProperty("array").GetRawText());
        Assert.Equal("{\"document\":[1,2]}", row.GetProperty("document").GetRawText());
        Assert.Equal("[\"element\",3]", row.GetProperty("element").GetRawText());
        Assert.Equal("AP8=", row.GetProperty("bytes").GetString());
        Assert.Equal("{\"text\":true}", row.GetProperty("text").GetString());
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    private static async Task<ExportOutcome> RunCommand(Task command, ExportOperationViewModel state)
    {
        await command;
        return state.Outcome!;
    }

    private sealed class PendingPicker : IExportPathPicker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickSavePathAsync(string name, ExportFileType type)
        {
            Entered.SetResult();
            return Completion.Task;
        }
    }
}
