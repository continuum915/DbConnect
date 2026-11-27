using System;

namespace DbConnect.Services;

/// <summary>Reuses a live string for the immediate inverse edit without retaining query history.</summary>
internal sealed class QueryTextUndoCache
{
    private sealed record Transition(int Offset, string Removed, string Inserted, WeakReference<string>? Before);
    private Transition? _last;
    private Transition? _pending;
    private WeakReference<string>? _restore;

    public void Prepare(int offset, string removed, string inserted, QueryTextSnapshot current)
    {
        _restore = _last is { } last && last.Offset == offset &&
            last.Removed == inserted && last.Inserted == removed ? last.Before : null;
        _pending = new Transition(offset, removed, inserted,
            current.TryGetMaterializedText(out var text) ? new WeakReference<string>(text!) : null);
    }

    public QueryTextSnapshot? Complete()
    {
        var restore = _restore;
        _last = _pending;
        _pending = null;
        _restore = null;
        return restore != null && restore.TryGetTarget(out var text) ? QueryTextSnapshot.FromString(text) : null;
    }

    public void Clear() { _last = null; _pending = null; _restore = null; }
}
