using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class EditorReleasePerformanceTests
{
    [Fact]
    public async Task SupervisedReleaseReportsOpenAndClosedDiagnostics()
    {
        var run = await new ProcessSupervisor().RunAsync(new("editor.release", "representative", 65536),
            1, false, TimeSpan.FromSeconds(45));
        Assert.True(run.Outcome == "completed", run.Failure);
        var release = Assert.IsType<EditorReleaseObservations>(run.Editor?.Release);
        Assert.True(release.RedoAvailableBeforeClose);
        Assert.True(release.Detached);
        Assert.True(release.Open.ManagedBytes > 0);
        Assert.NotNull(release.Closed);
        Assert.True(release.Closed.ManagedBytes > 0);
        Assert.True(release.Open.EditorAlive && release.Open.ViewAlive && release.Open.DocumentAlive && release.Open.SnapshotAlive);
        Assert.True(release.Dispatcher.SampleCount >= 2);
        Assert.False(Directory.Exists(run.Editor!.IsolatedDirectory));
        var directory = Path.Combine(Path.GetTempPath(), "rain-release-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.WriteAsync(directory, [run], new(directory, [run.Settings.ScenarioId], 1, 45, 20));
            var report = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("Editor release after close", report);
            Assert.Contains("weak", report, StringComparison.OrdinalIgnoreCase);
            var partial = run.Editor with { Release = release with { Closed = null } };
            var failed = run with { Outcome = "timeout", Metrics = null, Failure = "deadline", Editor = partial };
            await ReportWriter.WriteAsync(directory, [failed], new(directory, [run.Settings.ScenarioId], 1, 45, 20));
            report = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("deadline", report);
            Assert.DoesNotContain("| closed |", report);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
