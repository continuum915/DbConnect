using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>
/// RAIN ticket 02 (headless-ui-tests) — regression coverage for the exact gap ticket 08 (RAIN-208)
/// left open: <see cref="QueryTabViewModelResultPresentationTests.QueryTabViewModel_RealProviderResult_ShowsTwoDistinctIdColumns"/>
/// proves the ViewModel carries two distinct "id" entries, but nothing proved the View's code-behind
/// (column generation, ordinal bindings) actually turns that into two rendered columns. This does.
/// </summary>
public class QueryTabViewHeadlessTests
{
    [AvaloniaFact]
    public void ErrorSpan_MovesEditorCaretToReportedToken()
    {
        var viewModel = new QueryTabViewModel { QueryText = "SELECT 1; SELECT missing" };
        var view = new QueryTabView { DataContext = viewModel };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;

        viewModel.ErrorSpan = new StatementSpan(17, 7);

        Assert.Equal(17, editor.CaretOffset);
        Assert.Equal(17, viewModel.CaretOffset);
    }

    [AvaloniaFact]
    public void ResultsGrid_DuplicateColumnNames_RendersAsTwoDistinctColumns()
    {
        // SELECT a.id, b.id: two columns named "id", told apart only by ordinal.
        var result = QueryTabViewModelResultPresentationTests.MakeResult(["id", "id"], [1, 2]);

        var viewModel = new QueryTabViewModel();
        viewModel.SetExecutionResults([result]);

        // Real View, real code-behind: DataContext assignment synchronously runs
        // QueryTabView.OnDataContextChanged -> UpdateResultsGrid, which is the exact code under test.
        var view = new QueryTabView { DataContext = viewModel };

        var grid = view.FindControl<DataGrid>("ResultsGrid");
        Assert.NotNull(grid);
        Assert.Equal(2, grid!.Columns.Count);
        Assert.Equal("id", grid.Columns[0].Header);
        Assert.Equal("id", grid.Columns[1].Header);

        var rows = (System.Collections.ObjectModel.ObservableCollection<IReadOnlyList<string>>)grid.ItemsSource!;
        var row = Assert.Single(rows);
        ((Panel)grid.Parent!).Children.Remove(grid);
        var window = new Window { Content = grid, Width = 640, Height = 400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var firstCell = Assert.IsType<ContentControl>(grid.Columns[0].GetCellContent(row));
            var secondCell = Assert.IsType<ContentControl>(grid.Columns[1].GetCellContent(row));
            Assert.Equal("1", Assert.IsType<TextBlock>(firstCell.Content).Text);
            Assert.Equal("2", Assert.IsType<TextBlock>(secondCell.Content).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResultsGrid_ColumnChooser_HidesAndShowsGridColumns_AcrossReruns()
    {
        var viewModel = new QueryTabViewModel();
        viewModel.SetExecutionResults([QueryTabViewModelResultPresentationTests.MakeResult(["Id", "Name"], [1, "Alpha"])]);
        var grid = new QueryTabView { DataContext = viewModel }.FindControl<DataGrid>("ResultsGrid")!;

        viewModel.ResultGrid.Columns[1].IsVisible = false;
        Assert.True(grid.Columns[0].IsVisible);
        Assert.False(grid.Columns[1].IsVisible);

        // Re-running a query with the same columns regenerates the grid columns; the hidden one stays hidden.
        viewModel.SetExecutionResults([QueryTabViewModelResultPresentationTests.MakeResult(["Id", "Name"], [2, "Bravo"])]);
        Assert.False(grid.Columns[1].IsVisible);

        viewModel.ResultGrid.ShowAllColumnsCommand.Execute(null);
        Assert.True(grid.Columns[1].IsVisible);
    }
}
