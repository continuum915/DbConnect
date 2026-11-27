using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    /// <summary>The file this tab is bound to; null for an untitled tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabToolTip))]
    private SqlFileDocument? _document;

    /// <summary>Whether the bound file changed or disappeared on disk. A missing file counts as unsaved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(FileStateMessage))]
    [NotifyPropertyChangedFor(nameof(HasFileStateMessage))]
    [NotifyPropertyChangedFor(nameof(IsFileChangedOnDisk))]
    private SqlFileDiskState _fileState;

    public string? FileStateMessage => FileState switch
    {
        SqlFileDiskState.ChangedOnDisk => "This file changed on disk.",
        SqlFileDiskState.Missing => "This file no longer exists on disk. Save to create it again.",
        _ => null
    };

    public bool HasFileStateMessage => FileStateMessage != null;

    public bool IsFileChangedOnDisk => FileState == SqlFileDiskState.ChangedOnDisk;

    public override string? TabToolTip => Document?.Path;

    /// <summary>
    /// An untitled, unedited tab that still shows the starter query and has run nothing, so opening a
    /// file can use it instead of adding a tab.
    /// </summary>
    internal bool IsPristine =>
        Document == null && !IsDirty && !IsExecuting && CurrentResult == null
        && ReferenceEquals(CleanQueryText, InitialQueryText);
}
