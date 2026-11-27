using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Notices when files bound to query tabs change on disk. A tab without edits reloads; a tab with
/// edits offers Reload or Keep Mine; a deleted file is reported and the tab counts as unsaved.
/// Changes come from a watcher per folder (debounced) and from <see cref="CheckAllAsync"/>, which
/// the window calls when it regains focus because watchers miss changes on some network drives.
/// A tab that is running a query is checked once the query finishes.
/// </summary>
public sealed partial class SqlFileMonitor : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly Action<Func<Task>> _postToUi;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<QueryTabViewModel> _subscribed = new();
    private readonly HashSet<QueryTabViewModel> _deferred = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="tabs">The window's tabs.</param>
    /// <param name="postToUi">Runs work on the UI thread; watcher events arrive on other threads.</param>
    public SqlFileMonitor(ObservableCollection<ViewModelBase> tabs, Action<Func<Task>> postToUi)
    {
        _tabs = tabs;
        _postToUi = postToUi;
        _tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>Re-checks every file-bound tab against its file.</summary>
    public async Task CheckAllAsync()
    {
        foreach (var tab in _tabs.OfType<QueryTabViewModel>().Where(t => t.Document != null).ToList())
            await CheckAsync(tab);
    }

    /// <summary>Compares one tab with its file and reloads or flags it.</summary>
    public async Task CheckAsync(QueryTabViewModel tab)
    {
        if (tab.Document is not { } document || !_tabs.Contains(tab)) return;
        if (tab.IsExecuting)
        {
            _deferred.Add(tab);
            return;
        }

        if (!File.Exists(document.Path))
        {
            tab.FileState = SqlFileDiskState.Missing;
            return;
        }
        if (!SqlFileService.HasChangedOnDisk(document))
        {
            if (tab.FileState == SqlFileDiskState.Missing) tab.FileState = SqlFileDiskState.InSync;
            return;
        }
        if (tab.FileState == SqlFileDiskState.ChangedOnDisk) return; // already asking

        if (tab.HasTextEdits) tab.FileState = SqlFileDiskState.ChangedOnDisk;
        else await ReloadAsync(tab);
    }

    /// <summary>Replaces the tab's text with the file, discarding unsaved edits.</summary>
    [RelayCommand]
    private Task Reload(QueryTabViewModel? tab) => tab == null ? Task.CompletedTask : ReloadAsync(tab);

    /// <summary>Keeps the tab's edits; the next save asks before overwriting the newer file.</summary>
    [RelayCommand]
    private static void KeepMine(QueryTabViewModel? tab)
    {
        if (tab?.FileState == SqlFileDiskState.ChangedOnDisk) tab.FileState = SqlFileDiskState.InSync;
    }

    private static async Task ReloadAsync(QueryTabViewModel tab)
    {
        if (tab.Document is not { } document) return;
        (string Text, SqlFileDocument Document) file;
        try
        {
            file = await SqlFileService.ReadAsync(document.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // still being written, or locked; the next event or focus check retries
        }
        tab.QueryText = file.Text;
        tab.ClearDirty();
        tab.Document = file.Document;
        tab.FileState = SqlFileDiskState.InSync;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not QueryTabViewModel tab) return;
        if (e.PropertyName == nameof(QueryTabViewModel.Document)) Sync();
        else if (e.PropertyName == nameof(QueryTabViewModel.IsExecuting) && !tab.IsExecuting && _deferred.Remove(tab))
            _ = CheckAsync(tab);
    }

    /// <summary>Subscribes to open query tabs and keeps one watcher per folder that holds an open file.</summary>
    private void Sync()
    {
        if (_disposed) return;
        var open = _tabs.OfType<QueryTabViewModel>().ToHashSet();
        foreach (var tab in open.Where(_subscribed.Add)) tab.PropertyChanged += OnTabPropertyChanged;
        foreach (var tab in _subscribed.Where(t => !open.Contains(t)).ToList())
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            _subscribed.Remove(tab);
            _deferred.Remove(tab);
        }

        var folders = open.Select(t => t.Document?.Path).OfType<string>()
            .Select(Path.GetDirectoryName).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _watchers.Keys.Where(f => !folders.Contains(f)).ToList())
        {
            _watchers[folder].Dispose();
            _watchers.Remove(folder);
        }
        foreach (var folder in folders.Where(f => !_watchers.ContainsKey(f) && Directory.Exists(f)))
            _watchers[folder] = CreateWatcher(folder);
    }

    private FileSystemWatcher CreateWatcher(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => { OnFileEvent(e.OldFullPath); OnFileEvent(e.FullPath); };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Called on a watcher thread; checks the affected tab on the UI thread after a short pause.</summary>
    private void OnFileEvent(string path)
    {
        // An exception thrown on a watcher thread would end the process; a missed event is caught
        // by the next focus check instead.
        try { _postToUi(() => CheckPathAsync(path)); }
        catch (Exception) { }
    }

    private async Task CheckPathAsync(string path)
    {
        if (!_pendingPaths.Add(path)) return;
        await Task.Delay(Debounce);
        _pendingPaths.Remove(path);
        foreach (var tab in _tabs.OfType<QueryTabViewModel>()
                     .Where(t => t.Document != null && SqlFileService.SamePath(t.Document.Path, path)).ToList())
            await CheckAsync(tab);
    }

    public void Dispose()
    {
        _disposed = true;
        _tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tab in _subscribed) tab.PropertyChanged -= OnTabPropertyChanged;
        _subscribed.Clear();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
