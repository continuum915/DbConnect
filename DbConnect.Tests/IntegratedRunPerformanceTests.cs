using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class IntegratedRunPerformanceTests
{
    [Theory]
    [InlineData("execute.integrated")]
    [InlineData("editor.paste-recovery")]
    public async Task SupervisedIntegratedScenarioVerifiesOutputAndRemovesItsProfile(string scenario)
    {
        var run = await new ProcessSupervisor().RunAsync(new(scenario, "representative", 65536),
            1, false, TimeSpan.FromSeconds(45));
        Assert.True(run.Outcome == "completed", run.Failure);
        var integrated = Assert.IsType<IntegratedRunObservations>(run.Editor?.Integrated);
        Assert.Equal(scenario, integrated.Operation);
        Assert.False(Directory.Exists(run.EditorDataDirectory));
        Assert.Null(run.Execution); // Direct executor reports remain distinct.
        Assert.False(run.Editor!.HighlightingCompletionClaimed);
        var setup = Assert.IsType<SetupMemoryObservation[]>(run.Setup);
        Assert.Contains(setup, sample => sample.Stage == "fixture-baseline" && sample.Boundary == "checkpoint");
        foreach (var stage in new[] { "workspace-construct", "view-construct", "window-construct", "window-show",
            "explicit-layout", "recovery-serialize", "recovery-snapshot-verification", "recovery-settle" })
        {
            Assert.Contains(setup, sample => sample.Stage == stage && sample.Boundary == "start");
            Assert.Contains(setup, sample => sample.Stage == stage && sample.Boundary == "end");
        }
        Assert.All(setup, sample =>
        {
            Assert.True(sample.ElapsedMilliseconds >= 0);
            Assert.True(sample.PrivateBytes > 0);
            Assert.True(sample.WorkingSetBytes > 0);
            Assert.True(sample.ManagedHeapBytes >= 0);
            Assert.True(sample.TotalAllocatedBytes >= 0);
            if (sample.Boundary != "checkpoint") Assert.NotNull(sample.ActivityId);
        });
        Assert.True(setup.Zip(setup.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds));
        if (scenario == "execute.integrated")
        {
            Assert.Contains(run.Phases!, phase => phase.Phase == "command-starting");
            var memory = Assert.IsType<SetupMemoryObservation[]>(run.RunMemory);
            Assert.Contains(memory, sample => sample.Stage == "post-ready-baseline" && sample.Boundary == "checkpoint");
            foreach (var stage in new[] { "resolve-source", "detect-parameters", "prepare-highlighter",
                "prepare-execution-spans", "execute-statements", "present-results" })
            {
                Assert.Contains(memory, sample => sample.Stage == stage && sample.Boundary == "start");
                Assert.Contains(memory, sample => sample.Stage == stage && sample.Boundary == "end");
            }
            Assert.NotNull(integrated.Execution);
            Assert.Equal(run.Fixture!.GoDirectives + 1, integrated.Execution.CommandCount);
            Assert.Equal(integrated.Execution.CommandCount, integrated.Execution.ResultCount);
            Assert.Null(integrated.PersistedBytes);
            Assert.True(integrated.HighlightUpdates > 0);
            Assert.True(integrated.HighlightUpdates <= integrated.Execution.CommandCount,
                "Highlight updates must not exceed the number of submitted batches.");
            var stages = Assert.IsType<QueryRunStageObservations[]>(integrated.Stages);
            Assert.Equal(new[] { "resolve-source", "detect-parameters", "prepare-highlighter", "execute-batch",
                "present-results", "navigate-error", "register-history", "render-queue-drain", "layout-render" },
                stages.Select(stage => stage.Stage));
            Assert.All(stages, stage =>
            {
                Assert.True(stage.StartMilliseconds >= 0);
                Assert.True(stage.DurationMilliseconds >= 0);
                var worker = stage.Stage is "resolve-source" or "detect-parameters" or "prepare-highlighter";
                Assert.Equal(!worker, stage.StartedOnDispatcher);
                Assert.Equal(!worker, stage.EndedOnDispatcher);
                Assert.True(stage.GcPauseMilliseconds >= 0);
                Assert.True(stage.AllocatedBytes >= 0);
            });
            for (var i = 1; i < stages.Length; i++)
                Assert.True(stages[i].StartMilliseconds >=
                    stages[i - 1].StartMilliseconds + stages[i - 1].DurationMilliseconds);
            var samples = Assert.IsType<DispatcherSample[]>(run.Editor.Dispatcher.Samples);
            Assert.Equal(run.Editor.Dispatcher.SampleCount, samples.Length);
            Assert.Equal(run.Editor.Dispatcher.QueueDelaysMilliseconds, samples.Select(sample => sample.DelayMilliseconds));
            Assert.Equal(samples.Max(sample => sample.CadenceDelayMilliseconds), run.Editor.Dispatcher.MaximumCadenceDelayMilliseconds);
            Assert.All(samples, sample =>
            {
                Assert.True(sample.CompletedMilliseconds >= sample.EnqueuedMilliseconds);
                Assert.Equal(sample.CompletedMilliseconds - sample.EnqueuedMilliseconds, sample.DelayMilliseconds, precision: 6);
                Assert.True(sample.CadenceDelayMilliseconds >= sample.DelayMilliseconds);
                Assert.True(sample.GcPauseMilliseconds >= 0);
                Assert.True(sample.GcPauseSincePreviousSampleMilliseconds >= 0);
            });
        }
        else
        {
            Assert.True(integrated.SnapshotVerified);
            Assert.True(integrated.PersistedBytes > 65536);
            Assert.Null(integrated.Execution);
            Assert.Null(integrated.Stages);
        }
        var directory = Path.Combine(Path.GetTempPath(), "rain-integrated-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ReportWriter.WriteAsync(directory, [run], new(directory, [scenario], 1, 45, 20));
            var report = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            Assert.Contains("Integrated Run and scheduled editor recovery", report);
            Assert.Contains("debounce is 2000 ms", report);
            Assert.Contains(scenario, report);
            Assert.Contains("Integrated Run stage timings", report);
            if (scenario == "execute.integrated")
            {
                Assert.Contains("present-results", report);
                Assert.Contains("Run memory boundaries", report);
                Assert.Contains("execute-statements", report);
            }
            Assert.Contains("Integrated setup memory boundaries", report);
            Assert.Contains("recovery-serialize", report);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
