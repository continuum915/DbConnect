using System.Diagnostics;
using Avalonia.Threading;

namespace DbConnect.PerformanceTests;

/// <summary>Fixed-cadence background production with one queued probe and no catch-up bursts.</summary>
public sealed class DispatcherProbe : IAsyncDisposable
{
    private readonly int _cadence;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RawSample> _samples = [];
    private long _origin = Stopwatch.GetTimestamp();
    private Task? _producer;
    private int _pending;
    private long _missed;
    private sealed record RawSample(long Due, long Enqueued, long Completed,
        double GcPauseMilliseconds, double TotalGcPauseMilliseconds);

    internal void SetOperationOrigin(long timestamp) => _origin = timestamp;

    public DispatcherProbe(int cadenceMilliseconds = 20) => _cadence = cadenceMilliseconds > 0
        ? cadenceMilliseconds : throw new ArgumentOutOfRangeException(nameof(cadenceMilliseconds));

    public async Task StartAsync()
    {
        await SynchronizeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _producer = Task.Run(() => ProduceAsync(started));
        await started.Task;
    }

    private async Task ProduceAsync(TaskCompletionSource started)
    {
        var origin = Stopwatch.GetTimestamp();
        started.TrySetResult();
        var cadenceTicks = Stopwatch.Frequency * _cadence / 1000d;
        long nextSlot = 1;
        try
        {
            while (true)
            {
                var due = origin + nextSlot * cadenceTicks;
                var remaining = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                var slot = Math.Max(nextSlot, (long)((Stopwatch.GetTimestamp() - origin) / cadenceTicks));
                Interlocked.Add(ref _missed, slot - nextSlot);
                nextSlot = slot + 1;
                if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
                {
                    Interlocked.Increment(ref _missed);
                    continue;
                }
                var queued = Stopwatch.GetTimestamp();
                var gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
                Dispatcher.UIThread.Post(() =>
                {
                    Record((long)due, queued, gcPause);
                    Volatile.Write(ref _pending, 0);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private Task SynchronizeAsync()
    {
        var queued = Stopwatch.GetTimestamp();
        var gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        return Dispatcher.UIThread.InvokeAsync(() => Record(queued, queued, gcPause), DispatcherPriority.Background).GetTask();
    }

    private void Record(long due, long queued, double gcPause)
    {
        var completed = Stopwatch.GetTimestamp();
        var totalGcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var sample = new RawSample(due, queued, completed, totalGcPause - gcPause, totalGcPause);
        lock (_samples) _samples.Add(sample);
    }

    public async Task<DispatcherObservations> FinishAsync()
    {
        _stop.Cancel();
        if (_producer != null) await _producer;
        // FIFO at Background priority drains the one possible outstanding probe before this final probe.
        await SynchronizeAsync();
        DispatcherSample[] samples;
        lock (_samples)
        {
            var previousGc = _samples[0].TotalGcPauseMilliseconds;
            samples = _samples.Select(sample =>
            {
                var observed = new DispatcherSample(
                    Stopwatch.GetElapsedTime(_origin, sample.Enqueued).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(_origin, sample.Completed).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(sample.Enqueued, sample.Completed).TotalMilliseconds,
                    sample.GcPauseMilliseconds,
                    Stopwatch.GetElapsedTime(_origin, sample.Due).TotalMilliseconds,
                    Math.Max(Stopwatch.GetElapsedTime(sample.Enqueued, sample.Completed).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(sample.Due, sample.Completed).TotalMilliseconds),
                    sample.TotalGcPauseMilliseconds - previousGc);
                previousGc = sample.TotalGcPauseMilliseconds;
                return observed;
            }).ToArray();
        }
        var values = samples.Select(sample => sample.DelayMilliseconds).ToArray();
        var sorted = values.Order().ToArray();
        double? Percentile(double fraction) => sorted.Length >= 20
            ? sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1] : null;
        return new(_cadence, values.Length, Interlocked.Read(ref _missed), values.Max(),
            Percentile(.5), Percentile(.95), values, samples, samples.Max(sample => sample.CadenceDelayMilliseconds));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_producer != null) await _producer;
        _stop.Dispose();
    }
}
