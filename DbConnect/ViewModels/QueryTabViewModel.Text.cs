using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    private static readonly QueryTextSnapshot InitialQueryText =
        QueryTextSnapshot.FromString("SELECT name FROM sqlite_master WHERE type='table';");

    internal QueryTextSnapshot CurrentQueryTextSnapshot { get; private set; } = InitialQueryText;
    internal long QueryTextRevision { get; private set; }

    private readonly QueryTextBaseline _cleanText = new(InitialQueryText);

    /// <summary>True when the text differs from the clean baseline.</summary>
    public override bool IsDirty => _cleanText.IsDirty || FileState == SqlFileDiskState.Missing;

    /// <summary>True when the text itself differs from the clean baseline.</summary>
    internal bool HasTextEdits => _cleanText.IsDirty;

    /// <summary>The text the tab was created, opened, restored or last saved with.</summary>
    internal QueryTextSnapshot CleanQueryText => _cleanText.Snapshot;

    /// <summary>Current SQL, cached once per immutable editor revision. Explicit assignments still update the editor.</summary>
    public string QueryText
    {
        get => CurrentQueryTextSnapshot.Text;
        set
        {
            if (QueryText == value) return;
            PublishEditorQueryText(QueryTextSnapshot.FromString(value), knownDifferent: true);
        }
    }

    /// <summary>Publishes an immutable editor snapshot without reading its full string on each edit.</summary>
    public void PublishEditorQueryText(QueryTextSnapshot snapshot, bool knownDifferent = false)
    {
        if (!knownDifferent && CurrentQueryTextSnapshot.ContentEquals(snapshot)) return;
        OnPropertyChanging(nameof(QueryText));
        CurrentQueryTextSnapshot = snapshot;
        QueryTextRevision++;
        if (_cleanText.Update(snapshot, QueryTextRevision))
            OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(QueryText));
    }

    /// <summary>
    /// Makes the current text the clean baseline, after it was created, opened, restored or saved.
    /// When saving, pass the text and revision that were written: edits made while the save was
    /// running then stay unsaved.
    /// </summary>
    public void ClearDirty(QueryTextSnapshot? savedText = null, long savedRevision = 0)
    {
        var wasDirty = IsDirty;
        if (savedText == null) _cleanText.Reset(CurrentQueryTextSnapshot, QueryTextRevision);
        else
        {
            _cleanText.Reset(savedText, savedRevision);
            _cleanText.Update(CurrentQueryTextSnapshot, QueryTextRevision);
        }
        if (wasDirty != IsDirty) OnPropertyChanged(nameof(IsDirty));
    }
}
