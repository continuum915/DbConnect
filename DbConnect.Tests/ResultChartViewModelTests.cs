using System.Linq;
using LiveChartsCore.SkiaSharpView;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class ResultChartViewModelTests
{
    private static QueryResult MakeResult(string[] columnNames, params object?[][] rows)
    {
        var result = new QueryResult();
        for (var i = 0; i < columnNames.Length; i++)
            result.ColumnInfos.Add(new ColumnInfo { Name = columnNames[i], Ordinal = i });
        foreach (var row in rows)
            result.ObjectRows.Add(row);
        return result;
    }

    [Fact]
    public void UpdateChart_ReadsColumnarModel_BuildsSeriesFromObjectRows()
    {
        var vm = new ResultChartViewModel();
        var result = MakeResult(["Category", "Total"], ["A", 10], ["B", 20]);

        vm.UpdateChart(result);

        Assert.Equal(["Category", "Total"], vm.AvailableColumns);
        Assert.Equal("Category", vm.XColumn);
        Assert.Equal("Total", vm.YColumn);
        Assert.True(vm.HasData);
        var series = Assert.IsType<ColumnSeries<double>>(Assert.Single(vm.ChartSeries!));
        Assert.Equal(new double[] { 10, 20 }, series.Values!.ToArray());
    }

    [Fact]
    public void UpdateChart_DuplicateColumnNames_DoesNotCrashAndPicksFirstOrdinal()
    {
        // SELECT a.id, b.id: the picker cannot distinguish two identically-named entries,
        // so charting falls back to the first matching ordinal instead of throwing.
        var vm = new ResultChartViewModel();
        var result = MakeResult(["id", "id"], [1, 2]);

        vm.UpdateChart(result);

        Assert.Equal(["id", "id"], vm.AvailableColumns);
        Assert.True(vm.HasData);
        var series = Assert.IsType<ColumnSeries<double>>(Assert.Single(vm.ChartSeries!));
        Assert.Equal(new double[] { 1 }, series.Values!.ToArray());
    }

    [Fact]
    public void UpdateChart_EmptyResult_ClearsChartAndHasData()
    {
        var vm = new ResultChartViewModel();
        vm.UpdateChart(MakeResult(["Category", "Total"], ["A", 10]));
        Assert.True(vm.HasData);

        vm.UpdateChart(MakeResult(["Category", "Total"]));

        Assert.False(vm.HasData);
        Assert.Empty(vm.ChartSeries);
    }
}
