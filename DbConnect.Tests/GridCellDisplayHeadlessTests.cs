using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public class GridCellDisplayHeadlessTests
{
    [AvaloniaFact]
    public void QueryEditorResults_LargeValuesAreBounded()
    {
        var value = new string('x', 5 * 1024 * 1024);
        var result = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "payload", Ordinal = 0 }],
            ObjectRows = [new object?[] { value }]
        };
        var vm = new QueryTabViewModel();
        vm.SetExecutionResults([result]);
        var view = new QueryTabView { DataContext = vm };
        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        ((Panel)grid.Parent!).Children.Remove(grid);
        var window = new Window { Content = grid, Width = 640, Height = 400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == new string('x', 2048) + "…");
            Assert.Same(value, result.ObjectRows[0][0]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void QueryResultGrid_LargeJsonIsBoundedAfterScrolling_AndFullValueIsAvailable()
    {
        var value = "{\"payload\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        var result = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "payload", Ordinal = 0 }],
            ObjectRows = Enumerable.Range(0, 100).Select(_ => new object?[] { value }).ToList()
        };
        var view = new QueryResultView { DataContext = result };
        var grid = view.FindControl<DataGrid>("MainGrid")!;
        var window = new Window { Content = view, Width = 640, Height = 400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var last = grid.ItemsSource!.Cast<QueryResultDisplayRow>().Last();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            grid.ScrollIntoView(last, grid.Columns[0]);
            window.UpdateLayout();
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Scrolling a 5MiB value took {elapsed.Elapsed}.");
            var cell = grid.Columns[0].GetCellContent(last)!;
            var text = cell.GetVisualDescendants().OfType<TextBlock>().Single().Text!;
            Assert.Equal(value[..2048] + "…", text);
            Assert.Same(value, result.ObjectRows[99][0]);
            Assert.Same(value, last.RawValues[0]);
            Assert.Equal(value, last.DisplayValues[0]);
            Assert.Equal(value, CellValueViewerViewModel.TextOf(last.RawValues[0]));
            Assert.Equal(value, QueryResultFormattingService.BuildTabSeparated(["payload"], [last.DisplayValues], false));
            Assert.Contains(new string('x', 5 * 1024 * 1024), QueryResultFormattingService.BuildCsv(["payload"], [last.DisplayValues]));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TableGrid_LargeValueIsBounded_AndEditingKeepsTheFullText()
    {
        var value = new string('x', 5 * 1024 * 1024);
        var page = CreatePage([1]);
        page.Result.ObjectRows[0][1] = value;
        var vm = new TableDataTabViewModel("Customers", CreateSession(new TablePagingProvider(page)), CreateTableNode(true));
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);
        var view = new TableDataTabView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var grid = view.FindControl<DataGrid>("DataGrid")!;
            var column = grid.Columns.Single(c => Equals(c.Header, "Name"));
            var row = vm.Rows[0];
            Assert.Equal(new string('x', 2048) + "…", Assert.IsType<TextBlock>(column.GetCellContent(row)).Text);
            Assert.Same(value, row.Values["Name"]);
            Assert.Same(value, row.OriginalValues["Name"]);
            Assert.Contains(value, vm.BuildCopyRowsText([row]));
            grid.SelectedItem = row;
            grid.CurrentColumn = column;
            Assert.True(grid.BeginEdit());
            Dispatcher.UIThread.RunJobs();
            var content = column.GetCellContent(row)!;
            var editor = content.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal(value, editor.Text);
            Assert.True(grid.CommitEdit());
            Assert.Equal(value, row.Values["Name"]);
        }
        finally { window.Close(); }
    }
}
