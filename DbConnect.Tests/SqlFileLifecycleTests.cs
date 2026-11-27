using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>
/// File-backed SQL issues 03–05: recent files and Copy File Path, changes on disk, and restoring
/// file tabs after a restart or crash.
/// </summary>
public sealed class SqlFileLifecycleTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-filelife-{Guid.NewGuid():N}");
    private readonly List<string> _errors = [];
    private bool _overwrite;
    private int _overwriteAsked;

    public SqlFileLifecycleTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ── 03: recent files and Copy File Path ──────────────────────────────────

    [Fact]
    public void RecentFiles_AreMostRecentFirst_WithoutDuplicates_AndCapped()
    {
        var recent = new RecentFilesList();
        IReadOnlyList<string>? persisted = null;
        recent.Persist = list => persisted = list;

        for (var i = 0; i < RecentFilesList.Capacity + 2; i++) recent.Add(Path.Combine(_directory, $"{i}.sql"));
        recent.Add(Path.Combine(_directory, "3.sql"));

        Assert.Equal(RecentFilesList.Capacity, recent.Items.Count);
        Assert.Equal(Path.Combine(_directory, "3.sql"), recent.Items[0]);
        Assert.Single(recent.Items, p => p.EndsWith("3.sql"));
        Assert.DoesNotContain(recent.Items, p => p.EndsWith(Path.DirectorySeparatorChar + "0.sql"));
        Assert.Equal(recent.Items, persisted);
    }

    [Fact]
    public void RecentFiles_LoadFromAndSaveToSettings()
    {
        var persistence = new WorkspacePersistence(new ObservableCollection<ViewModelBase>(),
            new ObservableCollection<DatabaseSession>(), () => null, new QueryWorkspaceService(), settingsService: null)
        {
            Settings = new AppSettings { RecentFiles = ["a.sql", "b.sql"] }
        };
        var recent = new RecentFilesList();

        persistence.AttachRecentFiles(recent);
        Assert.Equal(["a.sql", "b.sql"], recent.Items);

        recent.Add("c.sql");
        Assert.Equal(["c.sql", "a.sql", "b.sql"], persistence.Settings.RecentFiles);
    }

    [Fact]
    public async Task OpeningAndSaving_AddToRecentFiles()
    {
        var vm = Create();
        var opened = Write("opened.sql", "SELECT 1;");
        var savedAs = Path.Combine(_directory, "saved.sql");
        vm.Files.PickSavePath = _ => Task.FromResult<string?>(savedAs);

        await vm.Files.OpenFilesAsync([opened]);
        vm.NewQueryTabWithContent("SELECT 2;");
        await vm.Files.SaveActiveCommand.ExecuteAsync(null);

        Assert.Equal([savedAs, opened], vm.Files.Recent.Items);
    }

    [Fact]
    public async Task OpeningAMissingRecentFile_ExplainsAndRemovesIt()
    {
        var vm = Create();
        var gone = Path.Combine(_directory, "gone.sql");
        vm.Files.Recent.Load([gone]);

        await vm.Files.OpenRecentCommand.ExecuteAsync(gone);

        Assert.Contains("no longer exists", Assert.Single(_errors));
        Assert.Empty(vm.Files.Recent.Items);
        Assert.Empty(vm.Tabs);
    }

    [AvaloniaFact]
    public void RecentMenu_ListsFiles_ThenClear_WhichIsDisabledWhenEmpty()
    {
        var vm = Create();
        var menu = new MenuItem();

        MainWindowDialogs.BuildRecentMenu(menu, vm.Files);
        var clear = Assert.IsType<MenuItem>(Assert.Single(menu.ItemsSource!.Cast<object>()));
        Assert.False(clear.IsEnabled);

        vm.Files.Recent.Load([Path.Combine(_directory, "x.sql")]);
        MainWindowDialogs.BuildRecentMenu(menu, vm.Files);
        var items = menu.ItemsSource!.Cast<object>().ToList();
        Assert.Equal("x.sql", Assert.IsType<MenuItem>(items[0]).Header);
        Assert.IsType<Separator>(items[1]);
        Assert.True(Assert.IsType<MenuItem>(items[2]).IsEnabled);
    }

    [AvaloniaFact]
    public async Task CopyFilePath_IsOfferedOnlyForFileTabs_AndCopiesThePath()
    {
        var vm = Create();
        var untitled = vm.NewQueryTabWithContent("SELECT 1;");
        var path = Write("copy.sql", "SELECT 1;");
        await vm.Files.OpenFilesAsync([path]);
        var fileTab = vm.SelectedTab!;
        string? copied = null;

        Assert.DoesNotContain(Headers(TabStripBehavior.BuildMenu(vm, untitled, t => copied = t)), h => h == "Copy File Path");
        var item = TabStripBehavior.BuildMenu(vm, fileTab, t => copied = t).Items.OfType<MenuItem>()
            .Single(i => (string?)i.Header == "Copy File Path");
        item.Command!.Execute(null);

        Assert.Equal(path, copied);
    }

    // ── 04: changes on disk ──────────────────────────────────────────────────

    [Fact]
    public async Task FileChangedOnDisk_ReloadsATabWithoutEdits()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        Touch(path, "SELECT 2;");

        await vm.Files.Monitor.CheckAsync(tab);

        Assert.Equal("SELECT 2;", tab.QueryText);
        Assert.False(tab.IsDirty);
        Assert.Equal(SqlFileDiskState.InSync, tab.FileState);
    }

    [Fact]
    public async Task FileChangedOnDisk_WithEdits_AsksAndReloadDiscardsThem()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        tab.QueryText = "SELECT mine;";
        Touch(path, "SELECT theirs;");

        await vm.Files.Monitor.CheckAsync(tab);
        Assert.Equal(SqlFileDiskState.ChangedOnDisk, tab.FileState);
        Assert.Equal("SELECT mine;", tab.QueryText);
        Assert.Equal("This file changed on disk.", tab.FileStateMessage);

        await vm.Files.Monitor.ReloadCommand.ExecuteAsync(tab);
        Assert.Equal("SELECT theirs;", tab.QueryText);
        Assert.False(tab.IsDirty);
        Assert.Null(tab.FileStateMessage);
    }

    [Fact]
    public async Task KeepMine_KeepsEdits_AndTheNextSaveAsksBeforeOverwriting()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        tab.QueryText = "SELECT mine;";
        Touch(path, "SELECT theirs;");
        await vm.Files.Monitor.CheckAsync(tab);

        vm.Files.Monitor.KeepMineCommand.Execute(tab);
        Assert.Equal(SqlFileDiskState.InSync, tab.FileState);
        Assert.True(tab.IsDirty);

        _overwrite = true;
        await vm.Files.SaveActiveCommand.ExecuteAsync(null);
        Assert.Equal(1, _overwriteAsked);
        Assert.Equal("SELECT mine;", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeletedFile_IsReported_TheTabCountsAsUnsaved_AndSaveRecreatesIt()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        File.Delete(path);

        await vm.Files.Monitor.CheckAsync(tab);
        Assert.Equal(SqlFileDiskState.Missing, tab.FileState);
        Assert.True(tab.IsDirty);
        Assert.Contains("no longer exists", tab.FileStateMessage);

        await vm.Files.SaveActiveCommand.ExecuteAsync(null);
        Assert.Equal("SELECT 1;", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
        Assert.Equal(SqlFileDiskState.InSync, tab.FileState);
        Assert.Equal(0, _overwriteAsked);
    }

    [Fact]
    public async Task DbConnectsOwnSave_IsNotTreatedAsAnExternalChange()
    {
        var (vm, tab, _) = await OpenAsync("SELECT 1;");
        tab.QueryText = "SELECT 2;";
        await vm.Files.SaveActiveCommand.ExecuteAsync(null);
        tab.QueryText = "SELECT 3;";

        await vm.Files.Monitor.CheckAsync(tab);

        Assert.Equal("SELECT 3;", tab.QueryText);
        Assert.Equal(SqlFileDiskState.InSync, tab.FileState);
    }

    [Fact]
    public async Task ATabRunningAQuery_IsCheckedWhenTheQueryFinishes()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        tab.IsExecuting = true;
        Touch(path, "SELECT 2;");

        await vm.Files.Monitor.CheckAsync(tab);
        Assert.Equal("SELECT 1;", tab.QueryText);

        tab.IsExecuting = false;
        for (var i = 0; i < 50 && tab.QueryText != "SELECT 2;"; i++) await Task.Delay(20);
        Assert.Equal("SELECT 2;", tab.QueryText);
    }

    [AvaloniaFact]
    public void ReplacingTheText_KeepsTheCaretLineWhereItStillExists()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT 1;\nSELECT 2;\nSELECT 3;" };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<AvaloniaEdit.TextEditor>("QueryEditor")!;
            editor.TextArea.Caret.Location = new AvaloniaEdit.Document.TextLocation(3, 4);

            vm.QueryText = "SELECT 10;\nSELECT 20;\nSELECT 30;\nSELECT 40;";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, editor.TextArea.Caret.Line);
            Assert.Equal(4, editor.TextArea.Caret.Column);

            vm.QueryText = "SELECT 1;";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, editor.TextArea.Caret.Line);
        }
        finally { window.Close(); view.DataContext = null; }
    }

    // ── 05: restore after restart and crash ──────────────────────────────────

    [Fact]
    public async Task CleanShutdown_CleanFileTab_ReloadsTheFileFromDisk()
    {
        var (vm, _, path) = await OpenAsync("SELECT 1;");
        var saved = Save(vm);
        Touch(path, "SELECT changed later;");

        var tab = RestoreSingle(saved);

        Assert.Equal("SELECT changed later;", tab.QueryText);
        Assert.Equal(path, tab.Document!.Path);
        Assert.Equal("restore.sql", tab.Title);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task CleanShutdown_FileTabWithEdits_RestoresTheEditsAsUnsaved()
    {
        var (vm, tab, path) = await OpenAsync("SELECT 1;");
        tab.QueryText = "SELECT edited;";

        var restored = RestoreSingle(Save(vm));

        Assert.Equal("SELECT edited;", restored.QueryText);
        Assert.Equal(path, restored.Document!.Path);
        Assert.True(restored.IsDirty);
        restored.QueryText = "SELECT 1;";
        Assert.False(restored.IsDirty); // the baseline is the file on disk
    }

    [Fact]
    public async Task Restore_MissingFile_KeepsTheTextBoundAndUnsaved()
    {
        var (vm, _, path) = await OpenAsync("SELECT 1;");
        var saved = Save(vm);
        File.Delete(path);

        var tab = RestoreSingle(saved);

        Assert.Equal("SELECT 1;", tab.QueryText);
        Assert.Equal(path, tab.Document!.Path);
        Assert.Equal(SqlFileDiskState.Missing, tab.FileState);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Restore_UntitledTabs_KeepTheirUnsavedState_AndOldSessionsLoadClean()
    {
        var vm = CreateMainWindowViewModel();
        WorkspaceSessionRestorer.Restore(vm, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs =
            [
                new SavedTab { TabType = "Query", Title = "Dirty", QueryText = "SELECT 1;", IsDirty = true },
                new SavedTab { TabType = "Query", Title = "Old", QueryText = "SELECT 2;" }
            ]
        });

        Assert.Equal([true, false], vm.Tabs.Select(t => t.IsDirty));
        Assert.All(vm.Tabs.OfType<QueryTabViewModel>(), t => Assert.Null(t.Document));
    }

    [Fact]
    public async Task CrashSnapshots_RecordTheFileAndUnsavedState()
    {
        var snapshot = new SessionSnapshot
        {
            Tabs = [new TabSnapshot { Title = "f.sql", QueryText = "SELECT 1;", FilePath = @"C:\sql\f.sql", IsDirty = true }]
        };
        using var stream = new MemoryStream();

        await RecoverySnapshotWriter.WriteAsync(stream, snapshot);

        Assert.Equal(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8.GetString(stream.ToArray()));
        var read = Assert.Single(JsonSerializer.Deserialize<SessionSnapshot>(stream.ToArray())!.Tabs);
        Assert.Equal(@"C:\sql\f.sql", read.FilePath);
        Assert.True(read.IsDirty);
    }

    [Fact]
    public void CrashRecovery_FileTab_RestoresUnsavedTextOverTheFile()
    {
        var path = Write("crash.sql", "SELECT 1;");
        var tab = new QueryTabViewModel();

        FileTabRestore.Apply(tab, "SELECT recovered;", path, wasDirty: true);

        Assert.Equal("SELECT recovered;", tab.QueryText);
        Assert.Equal(path, tab.Document!.Path);
        Assert.True(tab.IsDirty);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private MainWindowViewModel Create()
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        vm.Files.ConfirmOverwrite = _ => { _overwriteAsked++; return Task.FromResult(_overwrite); };
        vm.Files.ReportError = message => { _errors.Add(message); return Task.CompletedTask; };
        return vm;
    }

    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab, string Path)> OpenAsync(string text)
    {
        var vm = Create();
        var path = Write("restore.sql", text);
        await vm.Files.OpenFilesAsync([path]);
        return (vm, (QueryTabViewModel)vm.SelectedTab!, path);
    }

    private static SavedSession Save(MainWindowViewModel vm) => new()
    {
        WasCleanShutdown = true,
        Tabs = vm.Tabs.Select(SessionTabPersistence.Capture).OfType<SavedTab>().ToList()
    };

    private QueryTabViewModel RestoreSingle(SavedSession saved)
    {
        var vm = CreateMainWindowViewModel();
        WorkspaceSessionRestorer.Restore(vm, saved);
        return Assert.IsType<QueryTabViewModel>(Assert.Single(vm.Tabs));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Rewrites a file and moves its write time forward, as another program would.</summary>
    private static void Touch(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
    }

    private static IEnumerable<string?> Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => i.Header as string);
}
