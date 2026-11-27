using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class ResultChartViewModel : ViewModelBase
{
    public static string[] ChartTypes { get; } = ["Bar", "Line", "Pie"];

    [ObservableProperty] private string? _xColumn;
    [ObservableProperty] private string? _yColumn;
    [ObservableProperty] private string _chartType = "Bar";
    [ObservableProperty] private ISeries[] _chartSeries = [];
    [ObservableProperty] private bool _hasData;

    public ObservableCollection<string> AvailableColumns { get; } = new();

    private QueryResult? _lastResult;

    public ResultChartViewModel() { Title = "Chart"; }

    [RelayCommand]
    private void UpdateChartFromSelection()
    {
        if (_lastResult != null) UpdateChart(_lastResult);
    }

    public void UpdateChart(QueryResult result)
    {
        _lastResult = result;
        AvailableColumns.Clear();
        ChartSeries = [];
        HasData = false;

        if (result == null || !result.HasResultSet || result.ObjectRows.Count == 0)
            return;

        var names = result.ColumnInfos.Select(c => c.Name).ToList();
        foreach (var name in names) AvailableColumns.Add(name);

        if (string.IsNullOrWhiteSpace(XColumn) || !names.Contains(XColumn))
            XColumn = names.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(YColumn) || !names.Contains(YColumn))
            YColumn = names.Count > 1 ? names[1] : names.FirstOrDefault();

        BuildSeries(result);
    }

    private void BuildSeries(QueryResult result)
    {
        if (XColumn == null || YColumn == null) return;

        // Duplicate names resolve to their first matching ordinal, the same as the picker's
        // ItemsSource/SelectedItem binding, which cannot distinguish two identically-named entries.
        var xOrdinal = result.ColumnInfos.FindIndex(c => c.Name == XColumn);
        var yOrdinal = result.ColumnInfos.FindIndex(c => c.Name == YColumn);
        if (xOrdinal < 0 || yOrdinal < 0) return;

        var values = result.ObjectRows
            .Select(row =>
            {
                var raw = yOrdinal < row.Length ? row[yOrdinal] : null;
                return raw is double d ? d : raw is int i ? (double)i
                    : double.TryParse(raw?.ToString(), out var parsed) ? parsed : 0.0;
            })
            .ToArray();

        if (values.Length == 0) return;

        ISeries[] series = ChartType switch
        {
            "Pie" => values.Select((v, idx) =>
            {
                var row = result.ObjectRows[idx];
                var raw = xOrdinal < row.Length ? row[xOrdinal] : null;
                var label = raw?.ToString() ?? idx.ToString();
                return (ISeries)new PieSeries<double> { Values = new[] { v }, Name = label };
            }).ToArray(),
            "Line" => [new LineSeries<double> { Values = values, Name = YColumn }],
            _ => [new ColumnSeries<double> { Values = values, Name = YColumn }]
        };

        ChartSeries = series;
        HasData = true;
    }
}
