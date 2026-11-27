using System.Text.Json;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class EditorSelectionPerformanceTests
{
    [Fact]
    public async Task SupervisedSelectionReplacementUndoRedoValidatesEveryState()
    {
        var run = await new ProcessSupervisor().RunAsync(new("editor.selection-undo", "representative", 4096),
            1, false, TimeSpan.FromSeconds(30));
        Assert.True(run.Outcome == "completed", run.Failure);
        Assert.NotNull(run.Editor);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(run.Editor));
        var operations = json.RootElement.GetProperty("Operations").EnumerateArray().ToArray();
        Assert.Equal(new[] { "select", "replace", "undo", "redo" },
            operations.Select(operation => operation.GetProperty("Name").GetString()));
        Assert.Equal(new[] { 4096, 2057, 4096, 2057 },
            operations.Select(operation => operation.GetProperty("DocumentCharacters").GetInt32()));
        Assert.Equal(new[] { 2048, 0, 2048, 9 },
            operations.Select(operation => operation.GetProperty("SelectionLength").GetInt32()));
        Assert.Equal(new[] { 0, 1, 1, 1 },
            operations.Select(operation => operation.GetProperty("VersionAge").GetInt32()));
        Assert.Equal(new[] { 1024, 1033, 1024, 1024 },
            operations.Select(operation => operation.GetProperty("SelectionStart").GetInt32()));
        foreach (var operation in operations)
        {
            Assert.True(operation.GetProperty("Dispatcher").GetProperty("SampleCount").GetInt32() >= 2);
        }
        Assert.Equal(operations[0].GetProperty("DocumentSha256").GetString(),
            operations[2].GetProperty("DocumentSha256").GetString());
        Assert.Equal(operations[1].GetProperty("DocumentSha256").GetString(),
            operations[3].GetProperty("DocumentSha256").GetString());
        Assert.Equal(run.Metrics!.PostOperationHeapBytes, operations[3].GetProperty("PostOperationHeapBytes").GetInt64());
        Assert.False(run.Editor.HighlightingCompletionClaimed);
        Assert.False(Directory.Exists(run.Editor.IsolatedDirectory));
        var directory = Path.Combine(Path.GetTempPath(), "rain-selection-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var partialEditor = run.Editor with { Operations = run.Editor.Operations![..2] };
            var failed = run with { Outcome = "failed", Metrics = null, Failure = "validation failed", Editor = partialEditor };
            var timedOut = failed with { Outcome = "timeout", Failure = "deadline exceeded" };
            var differentProfile = run with { Settings = run.Settings with { Profile = "many-small-batches" } };
            await ReportWriter.WriteAsync(directory, [run, failed, timedOut, differentProfile],
                new(directory, [run.Settings.ScenarioId], 1, 30, 20));
            var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("## Selection, replacement, undo and redo", markdown);
            Assert.Contains("TextEditor.Select (programmatic range)", markdown);
            Assert.Contains("TextEditor.Redo (editor command API)", markdown);
            Assert.Contains("validation failed", markdown);
            Assert.Contains("deadline exceeded", markdown);
            Assert.Contains("| representative / 4096 / undo |", markdown);
            Assert.Contains("| many-small-batches / 4096 / undo |", markdown);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            Assert.Equal(4, report.RootElement.GetProperty("Runs")[0].GetProperty("Editor")
                .GetProperty("Operations").GetArrayLength());
            Assert.Equal(2, report.RootElement.GetProperty("Runs")[1].GetProperty("Editor")
                .GetProperty("Operations").GetArrayLength());
            var summary = Assert.Single(ReportWriter.Summarize([run, failed, timedOut]));
            Assert.Equal(1, summary.SuccessfulSamples);
            Assert.Equal(2, summary.FailedSamples);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
