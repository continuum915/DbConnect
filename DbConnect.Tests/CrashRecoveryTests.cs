using System;
using System.IO;
using System.Linq;
using DbConnect.Models;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

public class CrashRecoveryTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain_crash_{Guid.NewGuid():N}");

    public CrashRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Snapshot_HoldsOnlyUnsavedTabs()
    {
        var unsaved = new QueryTabViewModel { Title = "Unsaved" };
        unsaved.QueryText = "SELECT 1;";
        var saved = new QueryTabViewModel { Title = "Saved" };
        saved.QueryText = "SELECT 2;";
        saved.ClearDirty();

        var captured = WorkspacePersistence.CaptureUnsavedTabs([unsaved, saved]);

        var tab = Assert.Single(captured);
        Assert.Equal("Unsaved", tab.Title);
        Assert.True(tab.IsDirty);
        Assert.Empty(WorkspacePersistence.CaptureUnsavedTabs([saved]));
    }

    [Fact]
    public void Recovery_OpensUnsavedTabs_WithTheirText()
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();

        var count = CrashRecoveryRestorer.Apply(vm, [Snapshot(DateTimeOffset.UtcNow,
            new TabSnapshot { Title = "Report", QueryText = "SELECT report;" },
            new TabSnapshot { Title = "Blank", QueryText = "  " })]);

        Assert.Equal(1, count);
        var tab = Assert.IsType<QueryTabViewModel>(Assert.Single(vm.Tabs));
        Assert.Equal("Report", tab.Title);
        Assert.Equal("SELECT report;", tab.QueryText);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Recovery_UpdatesTabsAlreadyRestored_InsteadOfDuplicating()
    {
        var path = Path.Combine(_directory, "report.sql");
        File.WriteAllText(path, "SELECT on_disk;");
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        WorkspaceSessionRestorer.Restore(vm, new SavedSession
        {
            WasCleanShutdown = true,
            Tabs =
            [
                new SavedTab { TabType = "Query", Title = "Query 1", QueryText = "SELECT old;" },
                new SavedTab { TabType = "Query", Title = "report.sql", FilePath = path }
            ]
        });

        CrashRecoveryRestorer.Apply(vm, [Snapshot(DateTimeOffset.UtcNow,
            new TabSnapshot { Title = "Query 1", QueryText = "SELECT newer;", IsDirty = true },
            new TabSnapshot { Title = "report.sql", QueryText = "SELECT edited;", FilePath = path.ToUpperInvariant(), IsDirty = true })]);

        var tabs = vm.Tabs.Cast<QueryTabViewModel>().ToList();
        Assert.Equal(["SELECT newer;", "SELECT edited;"], tabs.Select(t => t.QueryText));
        Assert.All(tabs, t => Assert.True(t.IsDirty));
    }

    [Fact]
    public void Recovery_AppliesSnapshotsOldestFirst_SoTheNewestTextWins()
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        var now = DateTimeOffset.UtcNow;

        CrashRecoveryRestorer.Apply(vm,
        [
            Snapshot(now, new TabSnapshot { Title = "Q", QueryText = "SELECT newest;" }),
            Snapshot(now.AddHours(-1), new TabSnapshot { Title = "Q", QueryText = "SELECT oldest;" })
        ]);

        Assert.Equal("SELECT newest;", Assert.IsType<QueryTabViewModel>(Assert.Single(vm.Tabs)).QueryText);
    }

    private static SessionSnapshot Snapshot(DateTimeOffset at, params TabSnapshot[] tabs) =>
        new() { Pid = int.MaxValue, Timestamp = at, Tabs = [.. tabs] };
}
