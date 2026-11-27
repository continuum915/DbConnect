namespace DbConnect.Services;

/// <summary>
/// The clean baseline of a query editor: the text it was created, opened, restored or last saved
/// with. Decides whether newer text is unsaved without reading large texts on every edit.
/// </summary>
public sealed class QueryTextBaseline
{
    /// <summary>
    /// Above this length, an edit that leaves the text the same length as the baseline counts as a
    /// change without reading both texts, so typing in very large scripts stays cheap.
    /// </summary>
    public const int ExactCheckLimit = 1_000_000;

    private long _revision;

    public QueryTextBaseline(QueryTextSnapshot initial) => Snapshot = initial;

    public QueryTextSnapshot Snapshot { get; private set; }

    public bool IsDirty { get; private set; }

    /// <summary>Makes <paramref name="current"/> the clean text.</summary>
    public void Reset(QueryTextSnapshot current, long revision)
    {
        Snapshot = current;
        _revision = revision;
        IsDirty = false;
    }

    /// <summary>Re-evaluates <see cref="IsDirty"/> for newly published text; returns true when it changed.</summary>
    public bool Update(QueryTextSnapshot current, long revision)
    {
        var dirty = revision != _revision && (
            current.Length != Snapshot.Length
            || current.Length > ExactCheckLimit
            || !current.ContentEquals(Snapshot));
        if (dirty == IsDirty) return false;
        IsDirty = dirty;
        return true;
    }
}
