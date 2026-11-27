using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;

namespace DbConnect.ViewModels;

/// <summary>
/// Base class for all tab view models. Exposes <see cref="Title"/> so the
/// TabControl's ItemTemplate can bind to it generically, regardless of
/// whether the tab is a Query tab or a Table Data tab.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private string _title = string.Empty;
    public virtual string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public virtual bool IsDirty => false;

    /// <summary>Tooltip for the tab header, such as a file tab's full path. Null = none.</summary>
    public virtual string? TabToolTip => null;

    /// <summary>Pinned tabs stay at the start of the tab strip and are skipped by bulk close.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCloseButton))]
    private bool _isPinned;

    /// <summary>Whether the tab header shows a close button: not for pinned tabs or the permanent Objects tab.</summary>
    public virtual bool ShowsCloseButton => !IsPinned;

    /// <summary>Icon shown in the tab bar to distinguish tab types (see <see cref="DocumentTabIcons"/>).</summary>
    public virtual Symbol? TabIcon => DocumentTabIcons.For(GetType());

    /// <summary>Optional label color hex (e.g. "#E53935") from the connection's LabelColor. Null = no colored border.</summary>
    public virtual string? LabelColor => null;
}
