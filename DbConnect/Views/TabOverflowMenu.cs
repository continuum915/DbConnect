using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Avalonia;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// The document tab strip's overflow and scrolling (the <c>TabControl.document-tabs</c> template in
/// <c>DbConnect/Styles/Tabs.axaml</c>): the overflow button lists every tab, the selected tab scrolls into view, and the
/// mouse wheel scrolls the strip sideways.
/// </summary>
public static class TabOverflowMenu
{
    private const double WheelStep = 48;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsEnabled", typeof(TabOverflowMenu));

    public static readonly AttachedProperty<bool> ScrollsWithWheelProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("ScrollsWithWheel", typeof(TabOverflowMenu));

    static TabOverflowMenu()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            if (e.NewValue is not true) return;
            button.Click += (_, _) => Open(button);
            button.AttachedToVisualTree += (_, _) => FollowSelection(button);
        });
        ScrollsWithWheelProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            if (e.NewValue is true)
                viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        });
    }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);
    public static bool GetScrollsWithWheel(ScrollViewer viewer) => viewer.GetValue(ScrollsWithWheelProperty);
    public static void SetScrollsWithWheel(ScrollViewer viewer, bool value) => viewer.SetValue(ScrollsWithWheelProperty, value);

    /// <summary>One menu item per tab, in strip order; choosing one selects that tab.</summary>
    public static IReadOnlyList<MenuItem> Build(TabControl strip) =>
        strip.Items.Cast<object?>().Select(item => new MenuItem
        {
            Header = item is ViewModelBase tab ? tab.Title : item?.ToString(),
            Icon = item is ViewModelBase { TabIcon: { } icon } ? new SymbolIcon { Symbol = icon, Classes = { "inline" } } : null,
            Command = new RelayCommand(() => strip.SelectedItem = item),
            FontWeight = ReferenceEquals(item, strip.SelectedItem) ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
        }).ToList();

    private static void Open(Button button)
    {
        if (button.FindAncestorOfType<TabControl>() is not { } strip) return;
        new ContextMenu { ItemsSource = Build(strip), Placement = PlacementMode.BottomEdgeAlignedRight }.Open(button);
    }

    private static void FollowSelection(Button button)
    {
        if (button.FindAncestorOfType<TabControl>() is not { } strip) return;
        strip.SelectionChanged -= ScrollToSelected;
        strip.SelectionChanged += ScrollToSelected;
    }

    private static void ScrollToSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl strip) return;
        Dispatcher.UIThread.Post(() => strip.ContainerFromIndex(strip.SelectedIndex)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.Extent.Width <= viewer.Viewport.Width) return;
        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        var x = System.Math.Clamp(viewer.Offset.X - (delta * WheelStep), 0, viewer.Extent.Width - viewer.Viewport.Width);
        viewer.Offset = new Vector(x, viewer.Offset.Y);
        e.Handled = true;
    }
}
