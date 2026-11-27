using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class ExecutionStopPerformanceTests
{
    [Fact]
    public async Task SupervisedErrorReportsVerifiedStopInsteadOfThroughput()
    {
        var run = await new ProcessSupervisor().RunAsync(new("execute.error"), 1, false, TimeSpan.FromSeconds(15));
        Assert.True(run.Outcome == "completed", run.Failure);
        Assert.Null(run.Execution);
        var stop = Assert.IsType<ExecutionStopObservations>(run.Stop);
        Assert.Equal("error", stop.ObservedOutcome);
        Assert.Equal("DBCONNECT_FAKE_ERROR", stop.ErrorCode);
        Assert.Equal(2, stop.SubmittedCommands);
        Assert.True(stop.NoLaterSubmissions && stop.SessionDisposed);
        Assert.Contains(run.Phases!, phase => phase.Phase == "validated");
    }
    [Fact]
    public async Task SupervisedCancellationOccursOnlyAfterCommandIsBlocked()
    {
        var run = await new ProcessSupervisor().RunAsync(new("execute.cancel"), 1, false, TimeSpan.FromSeconds(15));
        Assert.True(run.Outcome == "completed", run.Failure);
        var stop = Assert.IsType<ExecutionStopObservations>(run.Stop);
        Assert.Equal("canceled", stop.ObservedOutcome);
        Assert.True(stop.IsCanceled);
        Assert.NotNull(stop.CancellationMilliseconds);
        Assert.True(stop.CancellationMilliseconds >= 0);
        Assert.Equal(2, stop.SubmittedCommands);
        Assert.Equal(2, stop.ResultCount);
        Assert.True(stop.NoLaterSubmissions && stop.SessionDisposed);
        Assert.Equal(0, stop.ActiveCommandsAfterCleanup);
        var phases = run.Phases!.Select(x => x.Phase).ToList();
        Assert.True(phases.IndexOf("command-blocked") < phases.IndexOf("cancellation-requested"));
        Assert.Contains("cleanup-completed", phases);
    }
    [Theory]
    [InlineData("harness.stop-cleanup-hang", "timeout")]
    [InlineData("harness.stop-cleanup-failure", "crashed")]
    public async Task SupervisorPreservesCleanupFailures(string scenario, string outcome)
    {
        var run = await new ProcessSupervisor().RunAsync(new(scenario), 1, false, TimeSpan.FromSeconds(5));
        Assert.Equal(outcome, run.Outcome);
        Assert.Equal("cleanup-started", run.LastCompletedPhase);
        Assert.Null(run.Metrics);
        Assert.NotEmpty(run.MemorySamples);
        Assert.Contains("execution-returned", run.Phases!.Select(x => x.Phase));
        Assert.Contains("cleanup-started", run.Failure);
        if (outcome == "crashed") Assert.Contains("Injected cleanup failure", run.Failure);
    }
    [Fact]
    public async Task ReportsLabelStopEvidenceAsOrchestrationCorrectness()
    {
        var run = await new ProcessSupervisor().RunAsync(new("execute.cancel"), 1, false, TimeSpan.FromSeconds(15));
        var directory = Path.Combine(Path.GetTempPath(), "rain-stop-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.WriteAsync(directory, [run], new(directory, [run.Settings.ScenarioId], 1, 15, 20));
            var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("Execution stop correctness", markdown);
            Assert.Contains("not real-provider/server interruption", markdown);
            Assert.Contains("execute.cancel", markdown);
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            Assert.True(json.RootElement.GetProperty("Runs")[0].GetProperty("Stop").GetProperty("NoLaterSubmissions").GetBoolean());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
