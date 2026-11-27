using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>Tier 1 §1.7: tab context-menu actions, pinning and reordering.</summary>
public sealed class TabStripActionsTests : MainWindowTestBase
{
    [Fact]
    public async Task CloseOthers_KeepsTheTabAndPinnedTabs()
    {
        var (vm, tabs) = Create(4);
        tabs[0].IsPinned = true;

        await vm.TabActions.CloseOthersCommand.ExecuteAsync(tabs[2]);

        Assert.Equal(new[] { tabs[0], tabs[2] }, vm.Tabs);
    }

    [Fact]
    public async Task CloseToTheRight_ClosesOnlyLaterUnpinnedTabs()
    {
        var (vm, tabs) = Create(4);

        await vm.TabActions.CloseToTheRightCommand.ExecuteAsync(tabs[1]);

        Assert.Equal(new[] { tabs[0], tabs[1] }, vm.Tabs);
    }

    [Fact]
    public async Task CloseAll_LeavesPinnedTabs()
    {
        var (vm, tabs) = Create(3);
        vm.TabActions.TogglePinCommand.Execute(tabs[1]);

        await vm.TabActions.CloseAllCommand.ExecuteAsync(null);

        Assert.Equal(new[] { tabs[1] }, vm.Tabs);
    }

    [Fact]
    public async Task CloseAll_WithNothingPinned_LeavesNoTabs()
    {
        var (vm, _) = Create(3);

        await vm.TabActions.CloseAllCommand.ExecuteAsync(null);

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
        Assert.True(vm.HasNoTabs);
    }

    [Fact]
    public async Task CancellingOneClose_StopsTheRest()
    {
        var (vm, tabs) = Create(2);
        var dirty = new DirtyTab();
        vm.Tabs.Insert(1, dirty);
        vm.RequestCloseConfirmation = _ => Task.FromResult(false);

        await vm.TabActions.CloseAllCommand.ExecuteAsync(null);

        // tabs[0] closes, the dirty tab is kept, and tabs[1] after it is not attempted.
        Assert.Equal(new ViewModelBase[] { dirty, tabs[1] }, vm.Tabs);
    }

    [Fact]
    public void Pinning_MovesTheTabToTheEndOfThePinnedBlock_AndUnpinningToTheStartOfTheRest()
    {
        var (vm, tabs) = Create(4);
        vm.TabActions.TogglePinCommand.Execute(tabs[2]);
        Assert.Equal(new[] { tabs[2], tabs[0], tabs[1], tabs[3] }, vm.Tabs);

        vm.TabActions.TogglePinCommand.Execute(tabs[3]);
        Assert.Equal(new[] { tabs[2], tabs[3], tabs[0], tabs[1] }, vm.Tabs);

        vm.TabActions.TogglePinCommand.Execute(tabs[2]);
        Assert.Equal(new[] { tabs[3], tabs[2], tabs[0], tabs[1] }, vm.Tabs);
        Assert.False(tabs[2].IsPinned);
    }

    [Fact]
    public void Move_StaysWithinThePinnedOrUnpinnedBlock_AndKeepsTheSelection()
    {
        var (vm, tabs) = Create(4);
        vm.TabActions.TogglePinCommand.Execute(tabs[0]);
        vm.SelectedTab = tabs[3];

        Assert.True(vm.TabActions.Move(tabs[3], 0));
        Assert.Equal(new[] { tabs[0], tabs[3], tabs[1], tabs[2] }, vm.Tabs);
        Assert.Same(tabs[3], vm.SelectedTab);

        Assert.False(vm.TabActions.Move(tabs[0], 3));
        Assert.Same(tabs[0], vm.Tabs[0]);
    }

    [Fact]
    public void PinnedState_IsSavedAndRestoredWithTheSession()
    {
        var (vm, tabs) = Create(2);
        vm.TabActions.TogglePinCommand.Execute(tabs[1]);
        var saved = new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = vm.Tabs.Select(SessionTabPersistence.Capture).OfType<SavedTab>().ToList()
        };
        var restored = CreateMainWindowViewModel();

        WorkspaceSessionRestorer.Restore(restored, saved);

        Assert.Equal(new[] { true, false }, restored.Tabs.Select(t => t.IsPinned));
    }

    [AvaloniaFact]
    public void ContextMenu_OffersTheActions_EnabledOnlyWhenTheyWouldDoSomething()
    {
        var (vm, tabs) = Create(2);
        vm.TabActions.TogglePinCommand.Execute(tabs[0]);

        var menu = TabStripBehavior.BuildMenu(vm, tabs[1]);
        var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header!, i => i.IsEnabled);

        Assert.Equal(
            new[] { "Close", "Close Others", "Close to the Right", "Close All", "Pin Tab" },
            items.Keys);
        Assert.False(items["Close Others"]);      // the only other tab is pinned
        Assert.False(items["Close to the Right"]); // nothing to the right
        Assert.True(items["Close All"]);
        Assert.Equal("Unpin Tab", TabStripBehavior.BuildMenu(vm, tabs[0]).Items.OfType<MenuItem>().Last().Header);
    }

    private (MainWindowViewModel Vm, List<QueryTabViewModel> Tabs) Create(int count)
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        var tabs = Enumerable.Range(0, count).Select(_ => vm.NewQueryTabWithSession()).ToList();
        return (vm, tabs);
    }

    private sealed class DirtyTab : ViewModelBase
    {
        public override bool IsDirty => true;
    }
}
