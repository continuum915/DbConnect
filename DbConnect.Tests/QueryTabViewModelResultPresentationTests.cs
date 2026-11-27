using LiveChartsCore.SkiaSharpView;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class QueryTabViewModelResultPresentationTests
{
    [Fact]
    public void QueryTabViewModel_FiftyThousandRows_LoadsWithoutBuildingHiddenChart()
    {
        var viewModel = new QueryTabViewModel();
        var result = new QueryResult
        {
            ColumnInfos =
            [
                new ColumnInfo { Name = "id", Ordinal = 0 },
                new ColumnInfo { Name = "value", Ordinal = 1 }
            ],
            ObjectRows = Enumerable.Range(0, 50_000)
                .Select(i => new object?[] { i, i * 2 })
                .ToList()
        };
        viewModel.Chart.ChartType = "Pie";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        viewModel.SetExecutionResults([result]);
        stopwatch.Stop();

        Assert.Equal(50_000, viewModel.DisplayResultRows.Count);
        Assert.Equal("99998", viewModel.DisplayResultRows[^1][1]);
        Assert.Empty(viewModel.Chart.ChartSeries);
        Assert.True(stopwatch.Elapsed < System.TimeSpan.FromSeconds(5),
            $"Presenting 50,000 rows took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void QueryTabViewModel_ChartBuildsWhenPaneOpens()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.SetExecutionResults([MakeResult(["label", "value"], ["A", 2])]);

        Assert.Empty(viewModel.Chart.ChartSeries);
        viewModel.ShowChartPane = true;

        Assert.True(viewModel.Chart.HasData);
        Assert.NotEmpty(viewModel.Chart.ChartSeries);

        viewModel.SetExecutionResults([MakeResult(["label", "value"], ["B", 5])]);
        var series = Assert.IsType<ColumnSeries<double>>(Assert.Single(viewModel.Chart.ChartSeries!));
        Assert.Equal([5.0], series.Values);
    }

    [Fact]
    public void QueryTabViewModel_OffersFiftyThousandRowCap()
    {
        var viewModel = new QueryTabViewModel();

        Assert.Contains(50_000, viewModel.ResultRowCapChoices);
        viewModel.ResultRowCap = 50_000;
        Assert.Equal(50_000, viewModel.ResultRowCap);
    }

    /// <summary>Shared by <see cref="QueryTabViewHeadlessTests"/> so the "two id columns" fixture has one definition.</summary>
    internal static QueryResult MakeResult(string[] columnNames, params object?[][] rows)
    {
        var result = new QueryResult();
        for (var i = 0; i < columnNames.Length; i++)
            result.ColumnInfos.Add(new ColumnInfo { Name = columnNames[i], Ordinal = i });
        foreach (var row in rows)
            result.ObjectRows.Add(row);
        return result;
    }

    [Fact]
    public void QueryTabViewModel_FiltersAndSortsDisplayRowsWithoutChangingRawResult()
    {
        var viewModel = new QueryTabViewModel();
        var result = MakeResult(
            ["Id", "Name"],
            [2, "Bravo"],
            [1, "Alpha"]);

        viewModel.CurrentResult = result;
        viewModel.ResultSortColumn = "Name";

        Assert.Equal(["Alpha", "Bravo"], viewModel.DisplayResultRows.Select(row => row[1]).ToArray());

        viewModel.ResultFilterText = "br";

        var filteredRow = Assert.Single(viewModel.DisplayResultRows);
        Assert.Equal("Bravo", filteredRow[1]);
        Assert.Equal(2, result.ObjectRows.Count);
    }

    [Fact]
    public void QueryTabViewModel_CopyBuildersUseDisplayedColumns()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = MakeResult(["Id", "Name"], [1, null]);

        var row = Assert.Single(viewModel.DisplayResultRows);
        Assert.Equal("NULL", viewModel.ResultGrid.BuildCellText(row, 1));
        Assert.Equal($"Id\tName{Environment.NewLine}1\tNULL", viewModel.ResultGrid.BuildRowsText([row]));
        Assert.Contains("Id\tName", viewModel.ResultGrid.BuildRowsText(viewModel.DisplayResultRows));
    }

    [Fact]
    public void QueryTabViewModel_CopyAndExportIncludeHeadersForEmptyResultSets()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = MakeResult(["Id", "Name"]);

        Assert.Equal("Id\tName", viewModel.ResultGrid.BuildRowsText(viewModel.DisplayResultRows));
        Assert.Equal($"Id,Name{Environment.NewLine}", viewModel.BuildCsvExport());
    }

    [Fact]
    public void QueryTabViewModel_DuplicateColumnNames_DisplayAsDistinctColumns()
    {
        // SELECT a.id, b.id: two columns named "id" must show their own value, not one collapsed value.
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = MakeResult(["id", "id"], [1, 2]);

        Assert.Equal(["id", "id"], viewModel.ResultColumns);

        var row = Assert.Single(viewModel.DisplayResultRows);
        Assert.Equal("1", row[0]);
        Assert.Equal("2", row[1]);
    }

    [Fact]
    public async Task QueryTabViewModel_RealProviderResult_ShowsTwoDistinctIdColumns()
    {
        // End-to-end through the real path production code uses (provider -> SetExecutionResults),
        // not the hand-built QueryResult the other tests use.
        var provider = new SqliteProvider();
        var result = await provider.ExecuteQueryAsync("Data Source=:memory:", "SELECT 1 AS id, 2 AS id;");

        var viewModel = new QueryTabViewModel();
        viewModel.SetExecutionResults([result]);

        Assert.Equal(["id", "id"], viewModel.ResultColumns);
        var row = Assert.Single(viewModel.DisplayResultRows);
        Assert.Equal("1", row[0]);
        Assert.Equal("2", row[1]);
    }
}
