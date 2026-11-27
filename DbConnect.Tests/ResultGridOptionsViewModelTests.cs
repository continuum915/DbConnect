using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class ResultGridOptionsViewModelTests
{
    private static readonly string NL = Environment.NewLine;

    [Fact]
    public void HiddenColumns_AreLeftOutOfCopiedRows()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = QueryTabViewModelResultPresentationTests.MakeResult(["Id", "Name", "Note"], [1, "Alpha", "x"]);
        var grid = viewModel.ResultGrid;

        grid.Columns[1].IsVisible = false;

        Assert.Equal([0, 2], grid.VisibleOrdinals);
        Assert.Equal($"Id\tNote{NL}1\tx", grid.BuildRowsText(viewModel.DisplayResultRows));
    }

    [Fact]
    public void ColumnVisibilityChanged_IsRaisedWhenAColumnIsToggled()
    {
        var grid = new ResultGridOptionsViewModel();
        grid.Load(["Id", "Name"], null);
        var raised = 0;
        grid.ColumnVisibilityChanged += () => raised++;

        grid.Columns[0].IsVisible = false;
        grid.ShowAllColumnsCommand.Execute(null);

        Assert.Equal(2, raised);
        Assert.All(grid.Columns, column => Assert.True(column.IsVisible));
    }

    [Fact]
    public void RerunningTheSameColumns_KeepsHiddenColumnsHidden_ButNewColumnsResetIt()
    {
        var grid = new ResultGridOptionsViewModel();
        grid.Load(["Id", "Name"], null);
        grid.Columns[1].IsVisible = false;

        grid.Load(["Id", "Name"], null);
        Assert.False(grid.Columns[1].IsVisible);

        grid.Load(["Id", "Email"], null);
        Assert.All(grid.Columns, column => Assert.True(column.IsVisible));
    }

    [Fact]
    public void CopyOptions_FlowIntoCopiedText_UsingRawValuesForSqlLiterals()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = QueryTabViewModelResultPresentationTests.MakeResult(["Id", "Name"], [1, "NULL"], [2, null]);
        var grid = viewModel.ResultGrid;
        grid.IncludeHeader = false;
        grid.Delimiter = CsvDelimiter.Comma;
        grid.SqlLiterals = true;

        Assert.Equal($"1,'NULL'{NL}2,NULL", grid.BuildRowsText(viewModel.DisplayResultRows));
        Assert.Equal("'NULL'", grid.BuildCellText(viewModel.DisplayResultRows[0], 1));
        Assert.Equal("NULL", grid.BuildCellText(viewModel.DisplayResultRows[1], 1));
    }

    [Fact]
    public void BuildCellText_ReturnsNullForMissingRowOrOrdinal()
    {
        var grid = new ResultGridOptionsViewModel();
        grid.Load(["Id"], null);

        Assert.Null(grid.BuildCellText(null, 0));
        Assert.Null(grid.BuildCellText(["1"], 3));
    }

    [Fact]
    public void BuildRowsText_IsNullWithoutColumns()
    {
        Assert.Null(new ResultGridOptionsViewModel().BuildRowsText([]));
    }

    [Fact]
    public void AStatementWithoutAResultSet_ClearsTheChooser_SoCopyDoesNotWriteTheOldHeader()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.CurrentResult = QueryTabViewModelResultPresentationTests.MakeResult(["Id"], [1]);

        viewModel.CurrentResult = new QueryResult { RecordsAffected = 3 };

        Assert.Empty(viewModel.ResultGrid.Columns);
        Assert.Null(viewModel.ResultGrid.BuildRowsText(viewModel.DisplayResultRows));
    }

    [Fact]
    public void Load_TakesTheLiteralDialectFromTheResultsConnection()
    {
        var grid = new ResultGridOptionsViewModel { SqlLiterals = true };
        var sqlServer = new SqlServerProvider().Capabilities;

        grid.Load(["Flag"], sqlServer);
        Assert.Same(sqlServer, grid.CopyOptions.Dialect);

        grid.Load(["Flag"], null);
        Assert.Null(grid.CopyOptions.Dialect);
    }
}
