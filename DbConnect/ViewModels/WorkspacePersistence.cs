using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Owns workspace snapshots, recovery, saved sessions, and persisted window state.</summary>
public sealed class WorkspacePersistence
{
    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly ObservableCollection<DatabaseSession> _sessions;
    private readonly Func<ViewModelBase?> _selectedTab;
    private readonly QueryWorkspaceService _queryWorkspaceService;
    private readonly SettingsService? _settingsService;
    private readonly SessionRecoveryService _recoveryService = new();
    private readonly SessionPersistenceService _persistenceService = new();
    private readonly System.Threading.Timer _recoveryTimer;
    private ViewModelBase? SelectedTab => _selectedTab();
    public AppSettings Settings { get; set; } = new();

    public WorkspacePersistence(
        ObservableCollection<ViewModelBase> tabs,
        ObservableCollection<DatabaseSession> sessions,
        Func<ViewModelBase?> selectedTab,
        QueryWorkspaceService queryWorkspaceService,
        SettingsService? settingsService)
    {
        _tabs = tabs;
        _sessions = sessions;
        _selectedTab = selectedTab;
        _queryWorkspaceService = queryWorkspaceService;
        _settingsService = settingsService;
        _recoveryTimer = new System.Threading.Timer(_ => _ = WriteSnapshotAsync(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void ScheduleSnapshotWrite()
    {
        _recoveryTimer?.Change(2000, System.Threading.Timeout.Infinite);
    }

    private async Task WriteSnapshotAsync()
    {
        try
        {
            var tabs = Dispatcher.UIThread.Invoke(() => CaptureUnsavedTabs(_tabs));

            if (tabs.Count == 0)
            {
                await _recoveryService.DeleteCurrentSnapshotAsync();
                return;
            }

            var snapshot = new SessionSnapshot
            {
                Pid = Environment.ProcessId,
                Timestamp = DateTimeOffset.UtcNow,
                Tabs = tabs,
                ActiveTabTitle = Dispatcher.UIThread.Invoke(() =>
                    (SelectedTab as QueryTabViewModel)?.Title)
            };
            await _recoveryService.WriteSnapshotAsync(snapshot);
        }
        catch { /* Recovery writes must never crash the app */ }
    }

    /// <summary>Only unsaved work needs recovering; with none, a crash leaves no snapshot behind.</summary>
    internal static List<TabSnapshot> CaptureUnsavedTabs(IEnumerable<ViewModelBase> tabs) =>
        tabs.OfType<QueryTabViewModel>()
            .Where(t => t.IsDirty)
            .Select(t => new TabSnapshot
            {
                Title = t.Title,
                QueryText = t.QueryText,
                ConnectionId = t.ConnectionIdToSave,
                SessionName = t.ConnectionNameToSave,
                FilePath = t.Document?.Path,
                IsDirty = true
            })
            .ToList();

    /// <summary>
    /// Hands the unsaved tabs of instances that did not close cleanly to <paramref name="restore"/>, then
    /// deletes their snapshots (the restored tabs are snapshotted again by this instance). Returns the number
    /// of tabs restored.
    /// </summary>
    public async Task<int> RecoverCrashedSessionsAsync(Func<IReadOnlyList<SessionSnapshot>, int> restore)
    {
        try
        {
            var orphans = await _recoveryService.TakeRecoverableSnapshotsAsync(DateTimeOffset.UtcNow);
            if (orphans.Count == 0) return 0;
            var restored = restore(orphans.Select(o => o.Snapshot).ToList());
            foreach (var orphan in orphans)
                await _recoveryService.DeleteSnapshotAsync(orphan.FilePath);
            return restored;
        }
        catch { return 0; /* Recovery must never stop the app starting */ }
    }

    public async Task SaveSessionAsync(bool wasCleanShutdown)
    {
        if (_settingsService == null || !Settings.RestorePreviousSession)
            return;

        var savedSession = new SavedSession
        {
            WasCleanShutdown = wasCleanShutdown,
            SavedAt = DateTime.UtcNow.ToString("O"),
            SelectedTabIndex = SelectedTab is null ? 0 : _tabs.IndexOf(SelectedTab),
            Tabs = _tabs.Select(SessionTabPersistence.Capture).OfType<SavedTab>().ToList()
        };

        await _persistenceService.SaveAsync(savedSession);
    }

    public async Task<SavedSession?> LoadSavedSessionAsync()
    {
        if (_settingsService == null || !Settings.RestorePreviousSession)
            return null;

        return await _persistenceService.LoadAsync();
    }

    public void SaveLayout(double width, double height, double x, double y, double splitterWidth)
    {
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        Settings.WindowX = x;
        Settings.WindowY = y;
        Settings.SplitterWidth = splitterWidth;
        if (_settingsService != null)
            _ = _settingsService.SaveAsync(Settings);
    }

    public (double Width, double Height, double X, double Y, double SplitterWidth) GetSavedLayout()
        => (Settings.WindowWidth, Settings.WindowHeight,
            Settings.WindowX, Settings.WindowY, Settings.SplitterWidth);

    public async Task SaveSidebarStateAsync(bool collapsed)
    {
        Settings.SidebarCollapsed = collapsed;
        if (_settingsService != null)
            await _settingsService.SaveAsync(Settings);
    }

    public bool GetSidebarCollapsed() => Settings.SidebarCollapsed;

    /// <summary>Makes loaded settings the ones in effect: preferences, then the recent-files list.</summary>
    public void Attach(AppSettings settings, RecentFilesList recent)
    {
        Settings = settings;
        AppPreferences.Apply(settings);
        AttachRecentFiles(recent);
    }

    /// <summary>Loads the recent-files list from settings and saves it there whenever it changes.</summary>
    public void AttachRecentFiles(RecentFilesList recent)
    {
        recent.Load(Settings.RecentFiles);
        recent.Persist = paths =>
        {
            Settings.RecentFiles = paths.ToList();
            if (_settingsService != null)
                _ = _settingsService.SaveAsync(Settings);
        };
    }

    public async Task OnCleanExitAsync(Func<Task> closeWorkspace)
    {
        _recoveryTimer.Dispose();
        await closeWorkspace();
        await _recoveryService.DeleteCurrentSnapshotAsync();
    }
}
