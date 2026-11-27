using System.Diagnostics;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public static class ChildScenario
{
    private static readonly Stopwatch Lifetime = Stopwatch.StartNew();
    public static Task<int> RunAsync(string scenario) => RunAsync(new RunSettings(scenario));

    public static async Task<int> RunAsync(RunSettings settings)
    {
        if (Debugger.IsAttached)
            throw new InvalidOperationException("Measurements require no debugger.");
        ScenarioSettings.Validate(settings);
        if (EditorPasteChild.IsEditor(settings.ScenarioId)) return await EditorPasteChild.RunAsync(settings);
        if (ExecutionStopChild.IsStop(settings.ScenarioId))
        {
            try { return await ExecutionStopChild.RunAsync(settings); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        var scenario = settings.ScenarioId;
        Send("initialized");
        var fixture = settings.Profile != "harness" ? RepresentativeImport.Generate(checked((int)settings.SizeBytes), settings.Profile) : null;
        var preparation = fixture is null || scenario is DirectBatchScenario.Id or RecoveryScenario.Id ? null : new ScriptPreparationScenario(scenario, fixture);
        var execution = scenario == DirectBatchScenario.Id ? new DirectBatchScenario(fixture!) : null;
        using var recovery = scenario == RecoveryScenario.Id ? new RecoveryScenario(fixture!, Path.Combine(Path.GetTempPath(), "DbConnect-performance-recovery-" + Guid.NewGuid().ToString("N"))) : null;
        Send("fixture-ready", fixture: fixture?.Metadata, recovery: recovery?.Context);
        if (await Console.In.ReadLineAsync() != "go")
            return 24;
        if (scenario == "harness.crash")
            Environment.Exit(23);
        if (scenario == "harness.hang")
            await Task.Delay(Timeout.Infinite);

        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        preparation?.Execute();
        if (recovery is not null) await recovery.ExecuteAsync();
        if (execution is not null) await execution.ExecuteAsync();
        if (scenario == "harness.idle")
            await Task.Delay(100);
        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        var heap = GC.GetTotalMemory(false);
        Send("operation");
        // Validation and diagnostic GC deliberately occur after the operation counters stop.
        var observations = preparation?.Validate();
        var executionObservations = execution?.Validate();
        var recoveryObservations = recovery is null ? null : await recovery.ValidateAsync();
        recovery?.Dispose();
        if (preparation is null && execution is null && recovery is null && scenario is not ("harness.noop" or "harness.idle"))
            throw new ArgumentException("Unknown scenario.");
        Send("validated");
        var diagnosticHeap = GC.GetTotalMemory(true);
        if (execution is not null) executionObservations = execution.MeasureRetention(executionObservations!);
        Send("complete", new OperationMetrics(duration, allocatedBytes, deltas, heap, diagnosticHeap), fixture?.Metadata, observations, executionObservations, recoveryObservations);
        GC.KeepAlive(preparation);
        GC.KeepAlive(fixture);
        return 0;
    }

    private static void Send(string phase, OperationMetrics? metrics = null,
        ImportMetadata? fixture = null, PreparationObservations? observations = null, ExecutionObservations? execution = null, RecoveryObservations? recovery = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChildMessage(phase, metrics, Lifetime.Elapsed.TotalMilliseconds, fixture, observations, execution, recovery)));
        Console.Out.Flush();
    }
}
