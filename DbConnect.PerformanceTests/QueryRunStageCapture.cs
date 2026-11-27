using System.Diagnostics;
using Avalonia.Threading;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record QueryRunStageObservations(string Stage, double StartMilliseconds,
    double DurationMilliseconds, bool StartedOnDispatcher, bool EndedOnDispatcher,
    double? GcPauseMilliseconds = null, long? AllocatedBytes = null);

/// <summary>Listens only during one integrated operation; contains no SQL or parameter values.</summary>
internal sealed class QueryRunStageCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<QueryRunStageObservations> _stages = [];
    private readonly long _origin;
    private readonly string _property = Guid.NewGuid().ToString("N");
    private sealed record StageStart(long Timestamp, bool Dispatcher, double GcPause, long Allocated);

    internal QueryRunStageCapture(long origin)
    {
        _origin = origin;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueryRunDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => activity.SetCustomProperty(_property,
                new StageStart(Stopwatch.GetTimestamp(), Dispatcher.UIThread.CheckAccess(),
                    GC.GetTotalPauseDuration().TotalMilliseconds, GC.GetTotalAllocatedBytes(false))),
            ActivityStopped = Record
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void Record(Activity activity)
    {
        if (activity.GetCustomProperty(_property) is not StageStart started)
            return;
        var stage = new QueryRunStageObservations(activity.OperationName,
            Stopwatch.GetElapsedTime(_origin, started.Timestamp).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started.Timestamp).TotalMilliseconds,
            started.Dispatcher, Dispatcher.UIThread.CheckAccess(),
            GC.GetTotalPauseDuration().TotalMilliseconds - started.GcPause,
            GC.GetTotalAllocatedBytes(false) - started.Allocated);
        lock (_stages) _stages.Add(stage);
    }

    internal QueryRunStageObservations[] Snapshot()
    {
        lock (_stages) return _stages.OrderBy(stage => stage.StartMilliseconds).ToArray();
    }

    public void Dispose() => _listener.Dispose();
}
