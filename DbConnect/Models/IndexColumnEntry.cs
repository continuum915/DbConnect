using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>
/// Represents a candidate column in the Index Manager form, with selection state and sort direction.
/// </summary>
public partial class IndexColumnEntry : ObservableObject
{
    public string ColumnName { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private IndexColumnDirection _direction = IndexColumnDirection.Asc;

    public string DirectionLabel => Direction == IndexColumnDirection.Asc ? "ASC" : "DESC";

    public IndexColumnEntry(string columnName) => ColumnName = columnName;

    partial void OnDirectionChanged(IndexColumnDirection value) =>
        OnPropertyChanged(nameof(DirectionLabel));

    public void ToggleDirection() =>
        Direction = Direction == IndexColumnDirection.Asc ? IndexColumnDirection.Desc : IndexColumnDirection.Asc;
}
