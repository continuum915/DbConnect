using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class PerformanceHarnessTests
{
    [Fact]
    public async Task ReportsBuildIdentityWhenLaunchedOutsideTheCheckout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rain-identity-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var process = new Process
            {
                StartInfo = new("dotnet")
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in new[] { typeof(ChildScenario).Assembly.Location, "--scenarios", "harness.noop",
                "--repetitions", "1", "--output", directory })
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["DBCONNECT_PERFORMANCE_ALLOW_DEBUG"] = "1";
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            Assert.True(process.ExitCode == 0, await error);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            var metadata = json.RootElement.GetProperty("Metadata");
            Assert.Matches("^[0-9a-f]{40}$", metadata.GetProperty("Commit").GetString()!);
            Assert.Contains(metadata.GetProperty("Dirty").GetString(), new[] { "clean", "dirty" });
            Assert.Equal("11.3.12", metadata.GetProperty("Packages").GetProperty("Avalonia").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CompletionDoesNotWaitForMemorySamplingCadence()
    {
        var run = await new ProcessSupervisor().RunAsync(new("harness.noop"), 1, false,
            TimeSpan.FromSeconds(2), 5000);
        Assert.Equal("completed", run.Outcome);
        Assert.NotNull(run.IdleMemory);
    }

    [Fact]
    public async Task ReportsRetainFailuresAndExcludeThemAndWarmupsFromStatistics()
    {
        var settings = new RunSettings("harness.noop");
        RunRecord Sample(double duration, bool warmup = false, string outcome = "completed") =>
            new(settings, 1, warmup, outcome, 0, "complete", [new(1, 200, 100)],
                new(duration, 64, [0, 0, 0], 128, 96), outcome == "completed" ? null : "failure", 10);
        var runs = new List<RunRecord> { Sample(999, true), Sample(1), Sample(3), Sample(8), Sample(1000, outcome: "timeout") };
        var summary = Assert.Single(ReportWriter.Summarize(runs));
        Assert.Equal(new Distribution(3, 1, 8), summary.DurationMilliseconds);
        Assert.Equal(3, summary.SuccessfulSamples);
        Assert.Equal(1, summary.FailedSamples);
        var directory = Path.Combine(Path.GetTempPath(), "rain-harness-" + Guid.NewGuid());
        try
        {
            await ReportWriter.WriteAsync(directory, runs, new(directory, [settings.ScenarioId], 3, 300, 20));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(5, json.RootElement.GetProperty("Runs").GetArrayLength());
            Assert.Contains("timeout", await File.ReadAllTextAsync(Path.Combine(directory, "report.md")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Assert.Null(Assert.Single(ReportWriter.Summarize([Sample(10, outcome: "crashed")])).DurationMilliseconds);
    }

    [Theory]
    [InlineData("harness.noop", "completed", 0, "complete")]
    [InlineData("harness.crash", "crashed", 23, "fixture-ready")]
    [InlineData("harness.hang", "timeout", null, "fixture-ready")]
    public async Task SupervisionPreservesSuccessfulAndFailedRuns(string scenario, string outcome, int? exitCode, string phase)
    {
        var run = await new ProcessSupervisor().RunAsync(new RunSettings(scenario), 1, false,
            TimeSpan.FromSeconds(scenario == "harness.hang" ? 2 : 20), 10);

        Assert.Equal(outcome, run.Outcome);
        if (exitCode.HasValue)
            Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal(phase, run.LastCompletedPhase);
        Assert.NotEmpty(run.MemorySamples);
        Assert.True(run.MemorySamples.Max(sample => sample.PrivateBytes) > 0);
        Assert.Equal(outcome == "completed", run.Metrics is not null);
    }
}
