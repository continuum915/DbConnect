using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>One editable row in the table data filter bar: column, operator, value.</summary>
public partial class TableFilterRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _column;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue))]
    private TableFilterOperator _operator = TableFilterOperator.Equals;

    [ObservableProperty]
    private string? _value;

    public object? ParameterValue { get; set; }

    partial void OnValueChanged(string? value) => ParameterValue = null;
    partial void OnColumnChanged(string? value) => ParameterValue = null;

    public bool NeedsValue => Operator is not (TableFilterOperator.IsNull or TableFilterOperator.IsNotNull);
}
