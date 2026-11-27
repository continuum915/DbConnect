using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Adds the tab context menu and drag reordering to the main tab strip. Attach with
/// <c>views:TabStripBehavior.IsEnabled="True"</c> on a TabControl whose DataContext is the
/// <see cref="MainWindowViewModel"/>.
/// </summary>
internal sealed class TabStripBehavior : AvaloniaObject
{
    private const double DragThreshold = 6;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TabStripBehavior, TabControl, bool>("IsEnabled");

    private static TabItem? _pressedItem;
    private static Point _pressPoint;
    private static bool _dragging;

    static TabStripBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<TabControl>((tabControl, e) =>
        {
            if (e.NewValue is not true) return;
            tabControl.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            tabControl.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            tabControl.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            tabControl.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        });
    }

    public static bool GetIsEnabled(TabControl element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TabControl element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TabControl { DataContext: MainWindowViewModel vm } tabControl
            || HeaderItem(e.Source) is not { DataContext: ViewModelBase tab } item
            || tab is ObjectsTabViewModel) // the permanent Objects tab has no close or pin actions
            return;

        BuildMenu(vm, tab, text => TopLevel.GetTopLevel(tabControl)?.Clipboard?.SetTextAsync(text)).Open(item);
        e.Handled = true;
    }

    /// <summary>The tab context menu for <paramref name="tab"/>; <paramref name="copyText"/> puts text on the clipboard.</summary>
    internal static ContextMenu BuildMenu(MainWindowViewModel vm, ViewModelBase tab, System.Action<string>? copyText = null)
    {
        var actions = vm.TabActions;
        var index = vm.Tabs.IndexOf(tab);
        var hasUnpinnedOthers = vm.Tabs.Any(t => !ReferenceEquals(t, tab) && !t.IsPinned);
        var hasUnpinnedToRight = vm.Tabs.Skip(index + 1).Any(t => !t.IsPinned);

        var items = new List<Control>
        {
            Item("Close", vm.CloseTabCommand, tab, enabled: true),
            Item("Close Others", actions.CloseOthersCommand, tab, hasUnpinnedOthers),
            Item("Close to the Right", actions.CloseToTheRightCommand, tab, hasUnpinnedToRight),
            Item("Close All", actions.CloseAllCommand, null, vm.Tabs.Any(t => !t.IsPinned)),
            new Separator(),
            Item(tab.IsPinned ? "Unpin Tab" : "Pin Tab", actions.TogglePinCommand, tab, enabled: true)
        };
        if (tab is QueryTabViewModel { Document: { } document })
        {
            items.Add(new Separator());
            items.Add(Item("Copy File Path", new RelayCommand(() => copyText?.Invoke(document.Path)), null, copyText != null));
        }
        return new ContextMenu { ItemsSource = items };
    }

    private static MenuItem Item(string header, System.Windows.Input.ICommand command, object? parameter, bool enabled) =>
        new() { Header = header, Command = command, CommandParameter = parameter, IsEnabled = enabled };

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedItem = null;
        _dragging = false;
        if (sender is not TabControl tabControl
            || !e.GetCurrentPoint(tabControl).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null)
            return;

        _pressedItem = HeaderItem(e.Source);
        _pressPoint = e.GetPosition(tabControl);
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem is not { DataContext: ViewModelBase tab } || tab is ObjectsTabViewModel
            || sender is not TabControl { DataContext: MainWindowViewModel vm } tabControl)
            return;
        if (!e.GetCurrentPoint(tabControl).Properties.IsLeftButtonPressed)
        {
            _pressedItem = null;
            return;
        }

        var position = e.GetPosition(tabControl);
        if (!_dragging && Distance(position, _pressPoint) < DragThreshold) return;
        _dragging = true;

        var target = tabControl.GetRealizedContainers().OfType<TabItem>()
            .FirstOrDefault(item => item.IsVisible && BoundsIn(item, tabControl).Contains(position));
        if (target?.DataContext is not ViewModelBase over || ReferenceEquals(over, tab) || over is ObjectsTabViewModel) return;

        vm.TabActions.Move(tab, vm.Tabs.IndexOf(over));
        e.Handled = true;
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) e.Handled = true;
        _pressedItem = null;
        _dragging = false;
    }

    /// <summary>
    /// The tab header under <paramref name="source"/>. The TabControl shows the selected content in
    /// its own presenter, outside any TabItem, so events from tab content return null.
    /// </summary>
    private static TabItem? HeaderItem(object? source) =>
        (source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true);

    private static Rect BoundsIn(Visual item, Visual relativeTo)
    {
        var origin = item.TranslatePoint(new Point(0, 0), relativeTo) ?? default;
        return new Rect(origin, item.Bounds.Size);
    }

    private static double Distance(Point a, Point b)
    {
        var d = a - b;
        return System.Math.Sqrt(d.X * d.X + d.Y * d.Y);
    }
}
