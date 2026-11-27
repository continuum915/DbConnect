using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Brings back the unsaved query text of an instance that did not close cleanly, without asking: each
/// recovered tab reopens unsaved. A tab that is already open (the same file, or an untitled tab of the same
/// title restored from the last session) takes the recovered text instead of opening a duplicate.
/// </summary>
public static class CrashRecoveryRestorer
{
    /// <summary>Applies <paramref name="snapshots"/> oldest first, so later text wins; returns the number of tabs recovered.</summary>
    public static int Apply(MainWindowViewModel vm, IEnumerable<SessionSnapshot> snapshots)
    {
        var recovered = 0;
        foreach (var snapshot in snapshots.OrderBy(s => s.Timestamp))
        {
            foreach (var saved in snapshot.Tabs ?? [])
            {
                if (saved is null || string.IsNullOrWhiteSpace(saved.QueryText))
                    continue;

                if (FindOpenTab(vm, saved) is { } open)
                {
                    // Differing text leaves the tab unsaved; identical text means nothing was lost.
                    open.QueryText = saved.QueryText;
                    vm.SelectedTab = open;
                }
                else
                {
                    var tab = vm.NewQueryTabWithSession(FindSession(vm, saved));
                    if (!string.IsNullOrWhiteSpace(saved.Title))
                        tab.Title = saved.Title;
                    // Recovered untitled text is always unsaved; a file tab keeps its recorded state.
                    FileTabRestore.Apply(tab, saved.QueryText, saved.FilePath, saved.FilePath == null || saved.IsDirty);
                    if (tab.SelectedSession == null)
                        tab.WaitingForConnection = RestoredConnection.From(saved.ConnectionId, saved.SessionName);
                }
                recovered++;
            }
        }
        return recovered;
    }

    private static QueryTabViewModel? FindOpenTab(MainWindowViewModel vm, TabSnapshot saved)
    {
        var queryTabs = vm.Tabs.OfType<QueryTabViewModel>();
        return saved.FilePath is { } path
            ? queryTabs.FirstOrDefault(t => string.Equals(t.Document?.Path, path, StringComparison.OrdinalIgnoreCase))
            : queryTabs.FirstOrDefault(t => t.Document == null && t.Title == saved.Title);
    }

    private static DatabaseSession? FindSession(MainWindowViewModel vm, TabSnapshot saved) =>
        saved.ConnectionId.HasValue || !string.IsNullOrEmpty(saved.SessionName)
            ? vm.ActiveSessionOptions.FirstOrDefault(s => saved.MatchesConnection(s.Connection))
            : null;
}
