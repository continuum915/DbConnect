using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Sizes the window's right-hand Information pane from <see cref="SidebarState"/>: the pane's column keeps the saved
/// width, hiding the pane collapses its column and splitter (the document column takes the room, the navigator keeps
/// its width), and a splitter drag stores the new width. Attach to the main content <see cref="Grid"/>, whose last two
/// columns are the pane's splitter and the pane.
/// </summary>
public static class InformationPaneLayout
{
    public static readonly AttachedProperty<SidebarState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Grid, SidebarState?>("State", typeof(InformationPaneLayout));

    private static readonly ConditionalWeakTable<Grid, PropertyChangedEventHandler> Handlers = new();

    static InformationPaneLayout()
    {
        StateProperty.Changed.AddClassHandler<Grid>((grid, e) =>
        {
            if (e.OldValue is SidebarState old) old.PropertyChanged -= Handler(grid);
            if (e.NewValue is not SidebarState state) return;
            state.PropertyChanged += Handler(grid);
            grid.AttachedToVisualTree -= OnAttached;
            grid.AttachedToVisualTree += OnAttached;
            Apply(grid, state);
        });
    }

    public static SidebarState? GetState(Grid grid) => grid.GetValue(StateProperty);
    public static void SetState(Grid grid, SidebarState? value) => grid.SetValue(StateProperty, value);

    private static PropertyChangedEventHandler Handler(Grid grid) =>
        Handlers.GetValue(grid, g => (sender, e) =>
        {
            if (sender is SidebarState state && e.PropertyName is nameof(SidebarState.InformationPaneVisible)
                    or nameof(SidebarState.InformationPaneWidth))
                Apply(g, state);
        });

    private static void Apply(Grid grid, SidebarState state)
    {
        if (grid.ColumnDefinitions.Count < 5) return;
        var splitter = grid.Children.OfType<GridSplitter>().FirstOrDefault(s => Grid.GetColumn(s) == grid.ColumnDefinitions.Count - 2);
        var pane = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == grid.ColumnDefinitions.Count - 1);
        if (splitter != null)
        {
            splitter.DragCompleted -= OnDragCompleted;
            splitter.DragCompleted += OnDragCompleted;
            splitter.IsVisible = state.InformationPaneVisible;
        }
        if (pane != null) pane.IsVisible = state.InformationPaneVisible;

        var splitterColumn = grid.ColumnDefinitions[^2];
        var paneColumn = grid.ColumnDefinitions[^1];
        splitterColumn.Width = new GridLength(state.InformationPaneVisible ? 5 : 0);
        paneColumn.MinWidth = state.InformationPaneVisible ? SidebarState.MinimumInformationPaneWidth : 0;
        paneColumn.Width = new GridLength(state.InformationPaneVisible ? state.InformationPaneWidth : 0);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && GetState(grid) is { } state) Apply(grid, state);
    }

    private static void OnDragCompleted(object? sender, VectorEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid grid } || GetState(grid) is not { } state) return;
        var width = grid.ColumnDefinitions[^1].ActualWidth;
        if (width == state.InformationPaneWidth) Apply(grid, state);
        else state.InformationPaneWidth = width;
    }
}
