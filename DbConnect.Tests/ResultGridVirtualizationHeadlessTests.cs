using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class ResultGridVirtualizationHeadlessTests
{
    [AvaloniaFact]
    public void EditorResultsGrid_FiveThousandRows_RealizesOnlyTheScrolledViewport()
    {
        var result = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "n", Ordinal = 0 }],
            ObjectRows = Enumerable.Range(0, 5_000).Select(i => new object?[] { i }).ToList()
        };
        var viewModel = new QueryTabViewModel();
        viewModel.SetExecutionResults([result]);
        var view = new QueryTabView { DataContext = viewModel };
        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        ((Panel)grid.Parent!).Children.Remove(grid);
        var window = new Window { Content = grid, Width = 640, Height = 400 };

        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.InRange(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), 1, 100);

            var lastRow = grid.ItemsSource!.Cast<object>().Last();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            grid.ScrollIntoView(lastRow, grid.Columns[0]);
            window.UpdateLayout();
            stopwatch.Stop();

            Assert.Contains(grid.GetVisualDescendants().OfType<DataGridRow>(),
                row => ReferenceEquals(row.DataContext, lastRow));
            Assert.InRange(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), 1, 100);
            Assert.True(stopwatch.Elapsed < System.TimeSpan.FromSeconds(2),
                $"Scrolling 5,000 rows took {stopwatch.Elapsed}.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FiveThousandRows_OnlyViewportRowsAreRealizedAfterScrolling()
    {
        var result = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "n", Ordinal = 0 }],
            ObjectRows = Enumerable.Range(0, 5_000).Select(i => new object?[] { i }).ToList()
        };
        var view = new QueryResultView { DataContext = result };
        var grid = view.FindControl<DataGrid>("MainGrid")!;
        var window = new Window { Content = view, Width = 640, Height = 400 };

        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.InRange(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), 1, 100);

            var lastRow = grid.ItemsSource!.Cast<object>().Last();
            grid.ScrollIntoView(lastRow, grid.Columns[0]);
            window.UpdateLayout();

            Assert.InRange(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), 1, 100);
            Assert.Contains(grid.GetVisualDescendants().OfType<DataGridRow>(),
                row => ReferenceEquals(row.DataContext, lastRow));
            Assert.Equal(4_999, ((QueryResultDisplayRow)lastRow).RawValues[0]);
        }
        finally
        {
            window.Close();
        }
    }
}
