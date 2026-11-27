using System.Text.Json;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class EditorNavigationPerformanceTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(65536)]
    public async Task SupervisedNavigationAndScrollingMovesTheViewportWithoutEditingText(int size)
    {
        var run = await new ProcessSupervisor().RunAsync(new("editor.navigation-scroll", "representative", size),
            1, false, TimeSpan.FromSeconds(30));
        Assert.True(run.Outcome == "completed", run.Failure);
        Assert.NotNull(run.Editor);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(run.Editor));
        var navigation = json.RootElement.GetProperty("Navigation");
        var operations = json.RootElement.GetProperty("Operations").EnumerateArray().ToArray();
        Assert.Equal(new[] { "navigate-middle", "scroll-down-1", "scroll-down-2", "scroll-down-3",
            "scroll-up-1", "scroll-up-2", "scroll-up-3", "navigate-end", "scroll-up-from-end", "scroll-down-to-end" },
            operations.Select(operation => operation.GetProperty("Name").GetString()));
        Assert.Equal(size, navigation.GetProperty("EndOffset").GetInt32());
        Assert.Equal(1, navigation.GetProperty("ViewportsPerScroll").GetInt32());
        Assert.Equal(0, navigation.GetProperty("InitialViewport").GetProperty("Y").GetDouble());
        Assert.True(navigation.GetProperty("InitialViewport").GetProperty("Height").GetDouble() > 0);
        Assert.Equal(navigation.GetProperty("MiddleOffset").GetInt32(), operations[0].GetProperty("CaretOffset").GetInt32());
        Assert.Equal(size, operations[7].GetProperty("CaretOffset").GetInt32());
        foreach (var operation in operations)
        {
            Assert.Equal(0, operation.GetProperty("VersionAge").GetInt32());
            Assert.Equal(size, operation.GetProperty("DocumentCharacters").GetInt32());
            Assert.Equal(run.Fixture!.Sha256, operation.GetProperty("DocumentSha256").GetString());
            Assert.Equal(0, operation.GetProperty("SelectionLength").GetInt32());
            var viewport = operation.GetProperty("Viewport");
            Assert.True(viewport.GetProperty("After").GetProperty("Height").GetDouble() > 0);
            Assert.True(viewport.GetProperty("After").GetProperty("VisualLinesValid").GetBoolean());
            Assert.True(operation.GetProperty("Dispatcher").GetProperty("SampleCount").GetInt32() >= 2);
        }
        foreach (var index in new[] { 0, 7 })
        {
            var viewport = operations[index].GetProperty("Viewport");
            var after = viewport.GetProperty("After");
            var targetLine = viewport.GetProperty("TargetLine").GetInt32();
            Assert.InRange(targetLine, after.GetProperty("FirstVisibleLine").GetInt32(), after.GetProperty("LastVisibleLine").GetInt32());
        }
        if (size == 65536)
        {
            Assert.True(operations[1].GetProperty("Viewport").GetProperty("After").GetProperty("Y").GetDouble() >
                operations[1].GetProperty("Viewport").GetProperty("Before").GetProperty("Y").GetDouble());
            Assert.True(operations[4].GetProperty("Viewport").GetProperty("After").GetProperty("Y").GetDouble() <
                operations[4].GetProperty("Viewport").GetProperty("Before").GetProperty("Y").GetDouble());
        }
        Assert.False(run.Editor.HighlightingCompletionClaimed);
        Assert.False(Directory.Exists(run.Editor.IsolatedDirectory));
        Assert.Equal(run.Metrics!.PostOperationHeapBytes, operations[^1].GetProperty("PostOperationHeapBytes").GetInt64());
        var directory = Path.Combine(Path.GetTempPath(), "rain-navigation-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var partial = run.Editor with { Operations = run.Editor.Operations![..2] };
            var failed = run with { Outcome = "failed", Metrics = null, Failure = "navigation validation failed", Editor = partial };
            var timedOut = failed with { Outcome = "timeout", Failure = "navigation deadline exceeded" };
            await ReportWriter.WriteAsync(directory, [run, failed, timedOut], new(directory, [run.Settings.ScenarioId], 1, 30, 20));
            var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("## Navigation and viewport scrolling", markdown);
            Assert.Contains("ScrollViewer.Offset", markdown);
            Assert.Contains("navigation validation failed", markdown);
            Assert.Contains("navigation deadline exceeded", markdown);
            Assert.DoesNotContain("## Selection, replacement, undo and redo", markdown);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            Assert.Equal(2, report.RootElement.GetProperty("Runs")[1].GetProperty("Editor").GetProperty("Operations").GetArrayLength());
            var summary = Assert.Single(ReportWriter.Summarize([run, failed, timedOut]));
            Assert.Equal(1, summary.SuccessfulSamples);
            Assert.Equal(2, summary.FailedSamples);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
