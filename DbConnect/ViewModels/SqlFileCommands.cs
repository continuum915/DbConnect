using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// File commands for query tabs: Open, Save, Save As, Save All and Add to Favorites. A query tab
/// opened from or saved to a file is bound to it; Save writes that file, and an untitled tab asks
/// for a path. Dialogs and pickers are supplied by the window.
/// </summary>
public sealed partial class SqlFileCommands
{
    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly Func<ViewModelBase?> _getSelected;
    private readonly Action<ViewModelBase> _select;
    private readonly Func<string, QueryTabViewModel> _newTabWithContent;

    public SqlFileCommands(
        ObservableCollection<ViewModelBase> tabs,
        Func<ViewModelBase?> getSelected,
        Action<ViewModelBase> select,
        Func<string, QueryTabViewModel> newTabWithContent,
        Action<Func<Task>>? postToUi = null)
    {
        Monitor = new SqlFileMonitor(tabs, postToUi ?? (work => Dispatcher.UIThread.Post(() => _ = work())));
        _tabs = tabs;
        _getSelected = getSelected;
        _select = select;
        _newTabWithContent = newTabWithContent;
    }

    public RecentFilesList Recent { get; } = new();

    /// <summary>Notices changes to open files on disk.</summary>
    public SqlFileMonitor Monitor { get; }

    /// <summary>Asks for SQL files to open; empty when cancelled.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickOpenPaths { get; set; }

    /// <summary>Asks where to save, given a suggested file name; null when cancelled.</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Asks whether to overwrite a file that changed on disk since it was opened; true to overwrite.</summary>
    public Func<string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>Shows why opening or saving failed.</summary>
    public Func<string, Task>? ReportError { get; set; }

    [RelayCommand]
    private async Task Open()
    {
        if (PickOpenPaths == null) return;
        await OpenFilesAsync(await PickOpenPaths());
    }

    /// <summary>Opens each file into its own tab, or selects the tab that already has it open.</summary>
    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var existing = _tabs.OfType<QueryTabViewModel>()
                .FirstOrDefault(t => t.Document != null && SqlFileService.SamePath(t.Document.Path, path));
            if (existing != null)
            {
                _select(existing);
                continue;
            }

            (string Text, SqlFileDocument Document) file;
            try
            {
                file = await SqlFileService.ReadAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Report($"Could not open \"{path}\": {ex.Message}");
                continue;
            }

            QueryTabViewModel tab;
            if (_getSelected() is QueryTabViewModel { IsPristine: true } pristine && _tabs.Contains(pristine))
            {
                tab = pristine;
                tab.QueryText = file.Text;
            }
            else
            {
                tab = _newTabWithContent(file.Text);
            }
            tab.ClearDirty();
            Bind(tab, file.Document);
            _select(tab);
        }
    }

    [RelayCommand]
    private async Task OpenRecent(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            Recent.Remove(path);
            await Report($"\"{path}\" no longer exists, so it was removed from the recent files.");
            return;
        }
        await OpenFilesAsync([path]);
    }

    [RelayCommand]
    private void ClearRecent() => Recent.Clear();

    [RelayCommand]
    private async Task SaveActive()
    {
        if (_getSelected() is { } tab) await SaveTabAsync(tab);
    }

    [RelayCommand]
    private async Task SaveActiveAs()
    {
        if (_getSelected() is QueryTabViewModel tab) await SaveAsAsync(tab);
    }

    [RelayCommand]
    private async Task SaveAll()
    {
        foreach (var tab in _tabs.Where(t => t.IsDirty).ToList())
        {
            if (!await SaveTabAsync(tab)) return;
        }
    }

    [RelayCommand]
    private void AddToFavorites()
    {
        if (_getSelected() is QueryTabViewModel tab) tab.SaveCurrentQueryCommand.Execute(null);
    }

    /// <summary>Saves a tab the way Save does for it; false when cancelled or failed.</summary>
    public async Task<bool> SaveTabAsync(ViewModelBase tab)
    {
        switch (tab)
        {
            case QueryTabViewModel queryTab:
                return queryTab.Document == null ? await SaveAsAsync(queryTab) : await SaveAsync(queryTab);
            case ProcedureEditorViewModel procedureEditor:
                await procedureEditor.SaveCommand.ExecuteAsync(null);
                return !procedureEditor.IsDirty;
            default:
                return !tab.IsDirty;
        }
    }

    private async Task<bool> SaveAsync(QueryTabViewModel tab)
    {
        var document = tab.Document!;
        if (SqlFileService.HasChangedOnDisk(document) && File.Exists(document.Path))
        {
            var overwrite = ConfirmOverwrite != null && await ConfirmOverwrite(
                $"\"{document.FileName}\" changed on disk after it was opened. Overwrite it with your version?");
            if (!overwrite) return false;
        }
        return await WriteAsync(tab, document.Path, document);
    }

    /// <summary>Asks for a path and saves there, binding the tab to the new file.</summary>
    public async Task<bool> SaveAsAsync(QueryTabViewModel tab)
    {
        if (PickSavePath == null) return false;
        var path = await PickSavePath(tab.Document?.FileName ?? SuggestedFileName(tab.Title));
        if (string.IsNullOrWhiteSpace(path)) return false;
        return await WriteAsync(tab, path, tab.Document);
    }

    private async Task<bool> WriteAsync(QueryTabViewModel tab, string path, SqlFileDocument? like)
    {
        var text = tab.CurrentQueryTextSnapshot;
        var revision = tab.QueryTextRevision;
        SqlFileDocument written;
        try
        {
            written = await SqlFileService.WriteAsync(path, text.Text, like);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Report($"Could not save \"{path}\": {ex.Message}");
            return false;
        }
        tab.ClearDirty(text, revision);
        Bind(tab, written);
        return true;
    }

    private void Bind(QueryTabViewModel tab, SqlFileDocument document)
    {
        tab.Document = document;
        tab.FileState = SqlFileDiskState.InSync;
        tab.Title = document.FileName;
        Recent.Add(document.Path);
    }

    private Task Report(string message) => ReportError?.Invoke(message) ?? Task.CompletedTask;

    private static string SuggestedFileName(string title)
    {
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (name.Length == 0) name = "Query";
        return name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name : name + ".sql";
    }
}
