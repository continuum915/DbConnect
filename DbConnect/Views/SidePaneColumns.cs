using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace DbConnect.Views;

/// <summary>
/// Opens and closes a side pane held in a <see cref="Grid"/>'s last two columns (a splitter, then the pane). Closed,
/// both columns take no room and the first column has the space; reopened, the pane gets back the width it was
/// dragged to. Set <c>views:SidePaneColumns.IsOpen</c> on the Grid.
/// </summary>
public static class SidePaneColumns
{
    public const double DefaultWidth = 280;
    public const double MinimumWidth = 180;

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Grid, bool>("IsOpen", typeof(SidePaneColumns));

    private static readonly AttachedProperty<double> WidthProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("PaneWidth", typeof(SidePaneColumns), DefaultWidth);

    static SidePaneColumns()
    {
        IsOpenProperty.Changed.AddClassHandler<Grid>((grid, e) => Apply(grid, e.NewValue is true));
        // A pane that starts closed raises no change; size it when the grid loads.
        Control.LoadedEvent.AddClassHandler<Grid>((grid, _) =>
        {
            if (grid.IsSet(IsOpenProperty)) Apply(grid, GetIsOpen(grid));
        });
    }

    public static bool GetIsOpen(Grid grid) => grid.GetValue(IsOpenProperty);
    public static void SetIsOpen(Grid grid, bool value) => grid.SetValue(IsOpenProperty, value);

    private static void Apply(Grid grid, bool open)
    {
        if (grid.ColumnDefinitions.Count < 3) return;
        var pane = grid.ColumnDefinitions[^1];
        if (!open && pane.ActualWidth >= MinimumWidth) grid.SetValue(WidthProperty, pane.ActualWidth);

        grid.ColumnDefinitions[^2].Width = new GridLength(open ? 5 : 0);
        pane.MinWidth = open ? MinimumWidth : 0;
        pane.Width = new GridLength(open ? grid.GetValue(WidthProperty) : 0);
        foreach (var child in grid.Children.Where(c => Grid.GetColumn(c) >= grid.ColumnDefinitions.Count - 2))
            child.IsVisible = open;
    }
}
