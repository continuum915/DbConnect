using System.Diagnostics;
using Avalonia.Controls;

namespace DbConnect.PerformanceTests;

public sealed record EditorOperationMeasurement(double SynchronousMilliseconds, double DurationMilliseconds,
    long AllocatedBytes, int[] CollectionsByGeneration, long PostOperationHeapBytes, EditorDeferredObservations Deferred)
{
    public static async Task<EditorOperationMeasurement> MeasureAsync(Window window, Action action)
    {
        await using var probe = new DispatcherProbe();
        await probe.StartAsync();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        action();
        var synchronous = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var deferred = await EditorDeferredWork.ObserveAsync(window, probe, start);
        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
        var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        var heap = GC.GetTotalMemory(false);
        return new(synchronous, duration, allocation, deltas, heap, deferred);
    }
}
