namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    partial void OnShowChartPaneChanged(bool value)
    {
        if (value && CurrentResult is { } result)
            _chart.UpdateChart(result);
    }
}
