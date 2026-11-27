using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LiveChartsCore.SkiaSharpView.Drawing;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class ResultChartViewHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyChart_CanBeMeasuredBeforeDataAndAfterClearing(bool clearExistingData)
    {
        var vm = new ResultChartViewModel();
        var view = new ResultChartView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            if (clearExistingData)
            {
                vm.UpdateChart(new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "Total", Ordinal = 0 }],
                    ObjectRows = [[10]]
                });
                window.UpdateLayout();
                vm.UpdateChart(new QueryResult());
            }
            var chart = view.GetVisualDescendants()
                .OfType<LiveChartsCore.SkiaSharpView.Avalonia.CartesianChart>().Single();
            var core = Assert.IsType<LiveChartsCore.CartesianChart<SkiaSharpDrawingContext>>(chart.CoreChart);
            // Exercise the same getter used by LiveCharts' queued measurement, without waiting for its timer.
            Assert.Empty(core.Series);
            Assert.False(vm.HasData);
        }
        finally { window.Close(); }
    }
}
