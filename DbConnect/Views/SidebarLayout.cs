using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Sizes the sidebar's lower region (Snippets) from <see cref="SidebarState"/>: the region's row
/// keeps the user's split height, so selecting a node never moves the tree; a splitter drag stores the new height;
/// with the section collapsed the row shrinks to its header. Attach to the sidebar <see cref="Grid"/>, whose
/// last row is the lower region and whose <see cref="GridSplitter"/> sits just above it.
/// </summary>
public static class SidebarLayout
{
    public static readonly AttachedProperty<SidebarState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Grid, SidebarState?>("State", typeof(SidebarLayout));

    private static readonly ConditionalWeakTable<Grid, PropertyChangedEventHandler> Handlers = new();

    static SidebarLayout()
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
        Handlers.GetValue(grid, g => (sender, _) =>
        {
            if (sender is SidebarState state) Apply(g, state);
        });

    private static void Apply(Grid grid, SidebarState state)
    {
        // Children may not exist yet when the state binding first resolves, so hook the splitter on every apply.
        if (grid.Children.OfType<GridSplitter>().FirstOrDefault() is { } splitter)
        {
            splitter.DragCompleted -= OnDragCompleted;
            splitter.DragCompleted += OnDragCompleted;
        }
        var lower = grid.RowDefinitions[^1];
        var tree = grid.RowDefinitions[^3];
        tree.Height = new GridLength(1, GridUnitType.Star);
        if (state.SnippetsExpanded)
        {
            lower.MinHeight = SidebarState.MinimumLowerHeight;
            lower.Height = new GridLength(state.LowerHeight);
        }
        else
        {
            lower.MinHeight = 0;
            lower.Height = GridLength.Auto;
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && GetState(grid) is { } state) Apply(grid, state);
    }

    private static void OnDragCompleted(object? sender, VectorEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid grid } || GetState(grid) is not { } state) return;
        var height = grid.RowDefinitions[^1].ActualHeight;
        if (height == state.LowerHeight) Apply(grid, state);
        else state.LowerHeight = height;
    }
}
