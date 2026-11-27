namespace DbConnect.PerformanceTests;

/// <summary>Polls the runtime's last-GC record so a long dispatcher delay can be compared with GC pauses. A poll can miss a GC that a later one replaces.</summary>
internal sealed class GcPauseObserver : IDisposable
{
    private readonly TimeSpan _totalAtStart = GC.GetTotalPauseDuration();
    private readonly Thread _thread;
    private volatile bool _stopped;
    private double _longest;

    public GcPauseObserver()
    {
        _thread = new Thread(Poll) { IsBackground = true, Name = "GC pause observer" };
        _thread.Start();
    }

    public double LongestMilliseconds => Volatile.Read(ref _longest);
    public double TotalMilliseconds => (GC.GetTotalPauseDuration() - _totalAtStart).TotalMilliseconds;

    private void Poll()
    {
        var lastIndex = GC.GetGCMemoryInfo(GCKind.Any).Index;
        while (!_stopped)
        {
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            if (info.Index != lastIndex)
            {
                lastIndex = info.Index;
                foreach (var pause in info.PauseDurations)
                    Volatile.Write(ref _longest, Math.Max(_longest, pause.TotalMilliseconds));
            }
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join();
    }
}
