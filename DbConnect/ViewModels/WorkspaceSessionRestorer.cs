using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Recreates workspace tabs from a cleanly saved session.</summary>
public static class WorkspaceSessionRestorer
{
    public static void Restore(MainWindowViewModel vm, SavedSession? saved)
    {
        if (saved is null || !saved.WasCleanShutdown || saved.Tabs.Count == 0)
            return;

        foreach (var savedTab in saved.Tabs)
        {
            if (savedTab.TabType == "Query")
            {
                DatabaseSession? session = null;
                if (savedTab.ConnectionId.HasValue || savedTab.ConnectionName is not null)
                    session = FindSession(vm, savedTab);

                var tab = vm.NewQueryTabWithSession(session);
                tab.Title = string.IsNullOrWhiteSpace(savedTab.Title) ? "Query" : savedTab.Title;
                FileTabRestore.Apply(tab, savedTab.QueryText ?? string.Empty, savedTab.FilePath, savedTab.IsDirty);
                if (session == null)
                    tab.WaitingForConnection = RestoredConnection.From(savedTab.ConnectionId, savedTab.ConnectionName);
                tab.IsPinned = savedTab.IsPinned;
                continue;
            }

            if (savedTab.TabType == "TableData" &&
                (savedTab.ConnectionId.HasValue || savedTab.ConnectionName is not null) &&
                (savedTab.Object != null || !string.IsNullOrWhiteSpace(savedTab.TableName)))
            {
                var session = FindSession(vm, savedTab);
                if (session != null)
                {
                    var name = savedTab.Object?.Name ?? savedTab.TableName!;
                    var tab = vm.NewTableDataTabWithSession(session, name);
                    tab.ObjectSelection.Restore(session, savedTab.Object, name);
                    tab.IsPinned = savedTab.IsPinned;
                }
            }
        }

        if (saved.SelectedTabIndex >= 0 && saved.SelectedTabIndex < vm.Tabs.Count)
            vm.SelectedTab = vm.Tabs[saved.SelectedTabIndex];
    }

    private static DatabaseSession? FindSession(MainWindowViewModel vm, SavedTab savedTab)
    {
        var candidates = vm.ActiveSessionOptions.Where(s => savedTab.MatchesConnection(s.Connection)).Take(2).ToArray();
        if (candidates.Length > 1)
            vm.StatusMessage = "Select the table again in the explorer: its saved connection name is ambiguous.";
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
