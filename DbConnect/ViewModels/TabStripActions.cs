using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

/// <summary>
/// Tab-strip operations: bulk close, pinning and reordering. Pinned tabs form a block at the
/// start of the strip; bulk close skips them, and reordering never moves a tab across the
/// boundary between pinned and unpinned tabs. Each close goes through the window's normal
/// close path, so unsaved-work and transaction prompts still apply; when the user cancels
/// one, the remaining closes stop.
/// </summary>
public sealed partial class TabStripActions
{
    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly Func<ViewModelBase, Task> _closeTab;
    private readonly Func<ViewModelBase?> _getSelected;
    private readonly Action<ViewModelBase> _select;

    public TabStripActions(
        ObservableCollection<ViewModelBase> tabs,
        Func<ViewModelBase, Task> closeTab,
        Func<ViewModelBase?> getSelected,
        Action<ViewModelBase> select)
    {
        _tabs = tabs;
        _closeTab = closeTab;
        _getSelected = getSelected;
        _select = select;
    }

    /// <summary>Closes a tab's database session, rolling back any open transaction.</summary>
    internal static Task ReleaseSessionAsync(ViewModelBase tab) => tab switch
    {
        QueryTabViewModel queryTab => queryTab.ReleaseDbSessionAsync(),
        TableDataTabViewModel tableTab => tableTab.ReleaseDbSessionAsync(),
        _ => Task.CompletedTask
    };

    [RelayCommand]
    private Task CloseOthers(ViewModelBase? tab) =>
        CloseInOrderAsync(_tabs.Where(t => !ReferenceEquals(t, tab) && !t.IsPinned));

    [RelayCommand]
    private Task CloseToTheRight(ViewModelBase? tab)
    {
        var index = tab == null ? -1 : _tabs.IndexOf(tab);
        return index < 0 ? Task.CompletedTask : CloseInOrderAsync(_tabs.Skip(index + 1).Where(t => !t.IsPinned));
    }

    [RelayCommand]
    private Task CloseAll() => CloseInOrderAsync(_tabs.Where(t => !t.IsPinned));

    [RelayCommand]
    private void TogglePin(ViewModelBase? tab)
    {
        if (tab == null || !_tabs.Contains(tab)) return;
        tab.IsPinned = !tab.IsPinned;
        // A newly pinned tab joins the end of the pinned block; an unpinned one leads the rest.
        MoveWithinBlock(tab, tab.IsPinned ? int.MaxValue : int.MinValue);
    }

    /// <summary>
    /// Moves <paramref name="tab"/> towards <paramref name="targetIndex"/>, clamped to its pinned or
    /// unpinned block. Returns true when the tab moved.
    /// </summary>
    public bool Move(ViewModelBase tab, int targetIndex) =>
        _tabs.Contains(tab) && MoveWithinBlock(tab, targetIndex);

    private bool MoveWithinBlock(ViewModelBase tab, int targetIndex)
    {
        var current = _tabs.IndexOf(tab);
        var pinnedOthers = _tabs.Count(t => t.IsPinned && !ReferenceEquals(t, tab));
        var (first, last) = tab.IsPinned ? (0, pinnedOthers) : (pinnedOthers, _tabs.Count - 1);
        var target = Math.Clamp(targetIndex, first, last);
        if (target == current) return false;

        var selected = _getSelected();
        _tabs.Move(current, target);
        // Some item hosts treat a move as remove + add and drop the selection; put it back.
        if (selected != null && !ReferenceEquals(_getSelected(), selected))
            _select(selected);
        return true;
    }

    private async Task CloseInOrderAsync(IEnumerable<ViewModelBase> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            if (!_tabs.Contains(tab)) continue;
            await _closeTab(tab);
            if (_tabs.Contains(tab)) return; // the user kept this tab open; stop closing
        }
    }
}
