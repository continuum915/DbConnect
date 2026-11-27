using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The table data tab's record pane (the design's TableData artboard, right column): the current row as a label/value
/// form that follows the grid, then the focused cell's full value with its column, type and foreign-key target.
/// </summary>
public sealed partial class RecordPaneState : ObservableObject
{
    private readonly TableDataTabViewModel _tab;
    private EditableRow? _row;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCell))]
    private string? _column;

    [ObservableProperty]
    private string _cellValue = string.Empty;

    [ObservableProperty]
    private string _cellDescription = string.Empty;

    [ObservableProperty]
    private bool _isForeignKeyCell;

    public RecordPaneState(TableDataTabViewModel tab)
    {
        _tab = tab;
        Record = new RecordViewModel(new TableRecordSource(tab));
    }

    /// <summary>The pane's own form over the tab's rows, following the grid's current row.</summary>
    public RecordViewModel Record { get; }

    public bool HasCell => Column != null;

    public string PositionText => Record.PositionText.Replace("Row ", "Record ", StringComparison.Ordinal);

    [RelayCommand]
    private void Toggle() => IsVisible = !IsVisible;

    /// <summary>Follows the grid: its current row (index and row) and focused column.</summary>
    public void Follow(int rowIndex, EditableRow? row, string? column)
    {
        if (!IsVisible) return;
        _row = row;
        if (rowIndex >= 0) Record.Show(rowIndex);
        OnPropertyChanged(nameof(PositionText));
        Column = column;
        RefreshCell();
    }

    [RelayCommand]
    private void SetNull()
    {
        if (Column == null || Record.Fields.FirstOrDefault(f => f.Name == Column) is not { IsEditable: true } field) return;
        field.SetNullCommand.Execute(null);
        RefreshCell();
    }

    private void RefreshCell()
    {
        if (Column == null || _row == null)
        {
            CellValue = string.Empty;
            CellDescription = string.Empty;
            IsForeignKeyCell = false;
            return;
        }

        CellValue = _row.Values.TryGetValue(Column, out var value) ? GridCellDisplay.FormatValue(value) : string.Empty;
        var reference = _tab.ForeignKeys.FirstOrDefault(fk => fk.Columns.Contains(Column));
        IsForeignKeyCell = reference != null;
        var description = $"{Column} · {_tab.GetColumnType(Column).ToString().ToLowerInvariant()}";
        if (reference != null)
        {
            var target = reference.ReferencedColumns[reference.Columns.ToList().IndexOf(Column)];
            description += $" · references {reference.ReferencedTable}.{target}";
        }

        CellDescription = description;
    }
}
