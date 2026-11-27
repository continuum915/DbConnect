using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record SetupMemoryObservation(string Stage, string Boundary, string? ActivityId,
    string? ParentId, double ElapsedMilliseconds, long WorkingSetBytes, long PrivateBytes,
    long ManagedHeapBytes, long TotalAllocatedBytes, double TotalGcPauseMilliseconds, bool OnDispatcher);

/// <summary>Flushes each boundary immediately so killed setup children retain partial evidence.</summary>
internal sealed class SetupMemoryCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Stopwatch _lifetime;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _gate = new();
    private bool _stopped;
    private bool _uiInitialized;
    private readonly bool _run;

    internal void MarkUiReady() => _uiInitialized = true;

    internal SetupMemoryCapture(Stopwatch lifetime, bool run = false)
    {
        _lifetime = lifetime;
        _run = run;
        _uiInitialized = run;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => run
                ? source.Name is QueryRunDiagnostics.SourceName or QueryMemoryDiagnostics.SourceName
                : source.Name == EditorSetupDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Record(activity.OperationName, "start", activity),
            ActivityStopped = activity => Record(activity.OperationName, "end", activity)
        };
        ActivitySource.AddActivityListener(_listener);
        Record(run ? "post-ready-baseline" : "fixture-baseline", "checkpoint", null);
    }

    internal SetupMemoryCapture CreateRunCapture() => new(_lifetime, run: true);

    private void Record(string stage, string boundary, Activity? activity)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _process.Refresh();
            var sample = new SetupMemoryObservation(stage, boundary, activity?.Id, activity?.ParentId,
                _lifetime.Elapsed.TotalMilliseconds, _process.WorkingSet64, _process.PrivateMemorySize64,
                GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(false), GC.GetTotalPauseDuration().TotalMilliseconds,
                _uiInitialized && Dispatcher.UIThread.CheckAccess());
            Console.WriteLine(JsonSerializer.Serialize(new ChildMessage((_run ? "run-memory-" : "setup-") + boundary,
                ChildElapsedMilliseconds: sample.ElapsedMilliseconds, Setup: _run ? null : sample,
                RunMemory: _run ? sample : null)));
            Console.Out.Flush();
        }
    }

    internal void Stop()
    {
        lock (_gate) _stopped = true;
        _listener.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
