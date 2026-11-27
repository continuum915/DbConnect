using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DbConnect.ViewModels;

public enum SaveChangesChoice { Save, Discard, Cancel }

/// <summary>
/// Asks before unsaved work is lost: Save / Don't Save / Cancel when closing a tab, and the same
/// for every unsaved tab when quitting. Save does what Save does for that tab (write its file, ask
/// for a path when untitled, or save a procedure). Discarding on quit reverts query tabs to their
/// clean text, so session restore does not bring back what the user chose to throw away.
/// </summary>
public sealed class UnsavedChangesGuard
{
    private readonly Func<IReadOnlyList<ViewModelBase>, Task<SaveChangesChoice>> _ask;
    private readonly Func<ViewModelBase, Task<bool>> _save;

    /// <param name="ask">Shows the Save / Don't Save / Cancel question for the given tabs.</param>
    /// <param name="save">Saves one tab; false when the user cancelled or the save failed.</param>
    public UnsavedChangesGuard(
        Func<IReadOnlyList<ViewModelBase>, Task<SaveChangesChoice>> ask,
        Func<ViewModelBase, Task<bool>> save)
    {
        _ask = ask;
        _save = save;
    }

    /// <summary>True when <paramref name="tab"/> may close: it is clean, saved, or the user chose not to save.</summary>
    public async Task<bool> ConfirmCloseAsync(ViewModelBase tab)
    {
        if (!tab.IsDirty) return true;
        return await _ask([tab]) switch
        {
            SaveChangesChoice.Discard => true,
            SaveChangesChoice.Save => await _save(tab),
            _ => false
        };
    }

    /// <summary>True when the app may quit: no unsaved tabs, all saved, or the user chose to discard them.</summary>
    public async Task<bool> ConfirmQuitAsync(IEnumerable<ViewModelBase> tabs)
    {
        var dirty = tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) return true;

        switch (await _ask(dirty))
        {
            case SaveChangesChoice.Discard:
                foreach (var queryTab in dirty.OfType<QueryTabViewModel>())
                {
                    queryTab.PublishEditorQueryText(queryTab.CleanQueryText, knownDifferent: true);
                    queryTab.ClearDirty();
                }
                return true;
            case SaveChangesChoice.Save:
                foreach (var tab in dirty)
                {
                    if (!await _save(tab)) return false;
                }
                return true;
            default:
                return false;
        }
    }
}
