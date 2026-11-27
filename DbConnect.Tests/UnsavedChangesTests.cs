using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>File-backed SQL issue 01: accurate unsaved state and Save / Don't Save / Cancel prompts.</summary>
public sealed class UnsavedChangesTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-unsaved-{Guid.NewGuid():N}");
    private readonly List<IReadOnlyList<string>> _asked = [];
    private SaveChangesChoice _answer = SaveChangesChoice.Cancel;
    private string? _pickedPath;
    private readonly List<string> _errors = [];

    public UnsavedChangesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ── Dirty tracking ────────────────────────────────────────────────────────

    [Fact]
    public void NewTab_IsClean_EditingDirties_AndEditingBackCleans()
    {
        var tab = new QueryTabViewModel();
        Assert.False(tab.IsDirty);
        var original = tab.QueryText;

        tab.QueryText = "SELECT 2";
        Assert.True(tab.IsDirty);

        tab.QueryText = original;
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void ClearDirty_MakesTheCurrentTextTheBaseline()
    {
        var tab = new QueryTabViewModel { QueryText = "SELECT 1" };
        tab.ClearDirty();

        tab.QueryText = "SELECT 2";
        Assert.True(tab.IsDirty);
        tab.QueryText = "SELECT 1";
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void VeryLargeSameLengthEdit_IsTreatedAsUnsavedWithoutComparingTheText()
    {
        var reads = 0;
        var large = new string('a', QueryTextBaseline.ExactCheckLimit + 1);
        var tab = new QueryTabViewModel();
        tab.PublishEditorQueryText(QueryTextSnapshot.FromString(large), knownDifferent: true);
        tab.ClearDirty();

        tab.PublishEditorQueryText(
            new QueryTextSnapshot(large.Length, () => large, () => { reads++; return new StringReader(large); }),
            knownDifferent: true);

        Assert.True(tab.IsDirty);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void GeneratedAndRestoredTabs_StartClean()
    {
        var vm = CreateMainWindowViewModel();
        Assert.False(vm.NewQueryTabWithContent("SELECT * FROM t").IsDirty);

        var restored = CreateMainWindowViewModel();
        WorkspaceSessionRestorer.Restore(restored, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [new SavedTab { TabType = "Query", Title = "Q", QueryText = "SELECT 42" }]
        });
        Assert.False(Assert.Single(restored.Tabs).IsDirty);
    }

    // ── Closing one tab ───────────────────────────────────────────────────────

    [Fact]
    public async Task ClosingACleanTab_DoesNotAsk()
    {
        var vm = CreateWired(out _);
        var tab = vm.NewQueryTabWithContent("SELECT 1");

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Empty(_asked);
        Assert.DoesNotContain(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingADirtyTab_Cancel_KeepsIt()
    {
        var vm = CreateWired(out _);
        var tab = DirtyTab(vm, "Report");

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Equal(["Report"], Assert.Single(_asked));
        Assert.Contains(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingADirtyTab_DontSave_ClosesIt()
    {
        var vm = CreateWired(out _);
        var tab = DirtyTab(vm, "Report");
        _answer = SaveChangesChoice.Discard;

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingADirtyTab_Save_WritesTheFileAndCloses()
    {
        var vm = CreateWired(out _);
        var tab = DirtyTab(vm, "Report");
        _answer = SaveChangesChoice.Save;
        _pickedPath = Path.Combine(_directory, "report.sql");

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Equal("SELECT 'changed'", await File.ReadAllTextAsync(_pickedPath));
        Assert.DoesNotContain(tab, vm.Tabs);
    }

    [Fact]
    public async Task ClosingADirtyTab_SaveThenCancelThePicker_KeepsIt()
    {
        var vm = CreateWired(out _);
        var tab = DirtyTab(vm, "Report");
        _answer = SaveChangesChoice.Save;
        _pickedPath = null;

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Contains(tab, vm.Tabs);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task SaveFailure_IsReported_AndTheTabStaysOpenAndUnsaved()
    {
        var vm = CreateWired(out _);
        var tab = DirtyTab(vm, "Report");
        _answer = SaveChangesChoice.Save;
        _pickedPath = Path.Combine(_directory, "missing-folder", "report.sql");

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Contains("Could not save", Assert.Single(_errors));
        Assert.Contains(tab, vm.Tabs);
        Assert.True(tab.IsDirty);
    }

    // ── Quitting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quit_AsksOnceAboutEveryUnsavedTab()
    {
        var vm = CreateWired(out var guard);
        vm.NewQueryTabWithContent("SELECT 1");
        DirtyTab(vm, "A");
        DirtyTab(vm, "B");

        Assert.False(await guard.ConfirmQuitAsync(vm.Tabs));

        Assert.Equal(["A", "B"], Assert.Single(_asked));
    }

    [Fact]
    public async Task Quit_DiscardAll_RevertsTabsSoSessionRestoreDoesNotBringTheTextBack()
    {
        var vm = CreateWired(out var guard);
        var tab = DirtyTab(vm, "A");
        _answer = SaveChangesChoice.Discard;

        Assert.True(await guard.ConfirmQuitAsync(vm.Tabs));

        Assert.Equal("SELECT 'original'", tab.QueryText);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Quit_SaveAll_StopsWhenOneSaveIsCancelled()
    {
        var vm = CreateWired(out var guard);
        DirtyTab(vm, "A");
        DirtyTab(vm, "B");
        _answer = SaveChangesChoice.Save;
        _pickedPath = null;

        Assert.False(await guard.ConfirmQuitAsync(vm.Tabs));
    }

    [Fact]
    public async Task Quit_WithNothingUnsaved_DoesNotAsk()
    {
        var vm = CreateWired(out var guard);
        vm.NewQueryTabWithContent("SELECT 1");

        Assert.True(await guard.ConfirmQuitAsync(vm.Tabs));
        Assert.Empty(_asked);
    }

    // ── Tab header ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TabHeader_ShowsTheUnsavedMarker_OnlyWhileTheTabIsDirty()
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        var tab = vm.NewQueryTabWithContent("SELECT 1");
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(Marker(window).IsVisible);

            tab.QueryText = "SELECT 2";
            Dispatcher.UIThread.RunJobs();
            Assert.True(Marker(window).IsVisible);
        }
        finally
        {
            tab.ClearDirty();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TabHeader_WithItsIcons_StaysOneRowTall()
    {
        // Fluent icons take all the height they are offered, so the header's icons need explicit sizes.
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        var tab = vm.NewQueryTabWithContent("SELECT 1");
        var window = new MainWindow { DataContext = vm, Height = 800 };
        try
        {
            window.Show();
            tab.QueryText = "SELECT 2";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var tabs = window.FindControl<TabControl>("MainTabControl")!;
            var header = tabs.GetVisualDescendants().OfType<TabItem>()
                .Single(t => t.FindAncestorOfType<TabControl>() == tabs && t.DataContext is not ObjectsTabViewModel);
            Assert.InRange(header.Bounds.Height, 1, 60);
        }
        finally
        {
            tab.ClearDirty();
            window.Close();
        }
    }

    private static Control Marker(Window window) =>
        window.FindControl<TabControl>("MainTabControl")!.GetVisualDescendants().OfType<Control>()
            .Single(c => c.Classes.Contains("dirty-marker") && c.DataContext is not ObjectsTabViewModel);

    private MainWindowViewModel CreateWired(out UnsavedChangesGuard guard)
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        vm.Files.PickSavePath = _ => Task.FromResult(_pickedPath);
        vm.Files.ReportError = message => { _errors.Add(message); return Task.CompletedTask; };
        guard = new UnsavedChangesGuard(
            tabs => { _asked.Add(tabs.Select(t => t.Title).ToList()); return Task.FromResult(_answer); },
            vm.Files.SaveTabAsync);
        vm.RequestCloseConfirmation = guard.ConfirmCloseAsync;
        return vm;
    }

    private static QueryTabViewModel DirtyTab(MainWindowViewModel vm, string title)
    {
        var tab = vm.NewQueryTabWithContent("SELECT 'original'");
        tab.Title = title;
        tab.QueryText = "SELECT 'changed'";
        return tab;
    }
}
