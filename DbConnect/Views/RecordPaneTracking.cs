using Avalonia;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Keeps a table data tab's record pane on the grid's current row and focused column. Set
/// <c>views:RecordPaneTracking.State="{Binding RecordPane}"</c> on the DataGrid.
/// </summary>
public static class RecordPaneTracking
{
    public static readonly AttachedProperty<RecordPaneState?> StateProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, RecordPaneState?>("State", typeof(RecordPaneTracking));

    static RecordPaneTracking()
    {
        StateProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            grid.SelectionChanged -= OnChanged;
            grid.CurrentCellChanged -= OnChanged;
            if (e.NewValue is not RecordPaneState state) return;
            grid.SelectionChanged += OnChanged;
            grid.CurrentCellChanged += OnChanged;
            state.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RecordPaneState.IsVisible)) Follow(grid);
            };
        });
    }

    public static RecordPaneState? GetState(DataGrid grid) => grid.GetValue(StateProperty);
    public static void SetState(DataGrid grid, RecordPaneState? value) => grid.SetValue(StateProperty, value);

    private static void OnChanged(object? sender, System.EventArgs e)
    {
        if (sender is DataGrid grid) Follow(grid);
    }

    private static void Follow(DataGrid grid) =>
        GetState(grid)?.Follow(grid.SelectedIndex, grid.SelectedItem as EditableRow, grid.CurrentColumn?.Header as string);
}
