using System;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Services;

namespace DbConnect.ViewModels;

internal sealed class RunningStatementHighlighter
{
    private readonly QueryTabViewModel _tab;
    private readonly string _sourceText;
    private readonly StatementSpan[] _spans;
    private readonly SynchronizationContext? _context;
    private readonly Timer? _progressTimer;
    private int _latestIndex = -1;
    private int _displayedIndex = -1;
    private int _pending;
    private int _cleared;

    public RunningStatementHighlighter(QueryTabViewModel tab, string sourceSql, int sourceOffset,
        SqlBatchDialect dialect, bool throttleUpdates = false)
        : this(tab, tab.QueryText, QueryScriptPreparation.HighlightSpans(sourceSql, sourceOffset, dialect,
            CancellationToken.None), throttleUpdates)
    {
    }

    internal RunningStatementHighlighter(QueryTabViewModel tab, string editorSql, StatementSpan[] spans,
        bool throttleUpdates = false)
    {
        _tab = tab;
        _tab.ErrorSpan = null;
        _sourceText = editorSql;
        _spans = spans;
        _context = SynchronizationContext.Current;
        // Small batches retain exact per-statement highlighting. Large batches publish the
        // latest statement every 100 ms, with at most one dispatcher callback outstanding.
        if (throttleUpdates && _context != null && _spans.Length > 128)
            _progressTimer = new Timer(_ => PublishLatest(), null, 100, 100);
    }

    public Task ShowAsync(int index)
    {
        if (index < 0 || index >= _spans.Length || Volatile.Read(ref _cleared) != 0)
            return Task.CompletedTask;

        Volatile.Write(ref _latestIndex, index);
        if (_progressTimer != null && index != 0)
            return Task.CompletedTask;

        if (_context == null)
        {
            if (Volatile.Read(ref _cleared) == 0) Show(index);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(_ =>
        {
            if (Volatile.Read(ref _cleared) == 0) Show(index);
            completion.SetResult();
        }, null);
        return completion.Task;
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _cleared, 1);
        _progressTimer?.Dispose();
        _tab.RunningStatementSpan = null;
    }

    private void PublishLatest()
    {
        if (Volatile.Read(ref _cleared) != 0 || Volatile.Read(ref _displayedIndex) < 0 ||
            Volatile.Read(ref _latestIndex) == Volatile.Read(ref _displayedIndex) ||
            Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return;

        _context!.Post(_ =>
        {
            try
            {
                if (Volatile.Read(ref _cleared) == 0)
                    Show(Volatile.Read(ref _latestIndex));
            }
            finally { Volatile.Write(ref _pending, 0); }
        }, null);
    }

    private void Show(int index)
    {
        Volatile.Write(ref _displayedIndex, index);
        if (_tab.QueryText == _sourceText)
            _tab.RunningStatementSpan = _spans[index];
    }
}
