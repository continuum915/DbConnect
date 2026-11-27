using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>
/// UI redesign 06: the query tab's result strip has one Result pane per result set, never more than
/// <see cref="ResultTabSelection.Limit"/>, so a batch of thousands of statements stays cheap to show; statements without
/// a result set are listed in Messages.
/// </summary>
public class QueryResultPickerHeadlessTests
{
    [AvaloniaFact]
    public void ThousandsOfStatementResults_KeepTheStripBounded_AndShowTheirMessages()
    {
        var results = Enumerable.Range(0, 5_000).Select(index => new QueryResult
        {
            TabLabel = $"Statement {index + 1}",
            RecordsAffected = index
        }).ToArray();
        var tab = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            tab.SetExecutionResults(results, results[^1]);
            window.UpdateLayout();

            var strip = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultStrip");
            Assert.Equal(5, strip.ItemCount); // Messages, Profile, Status, Explain, Chart: no result sets
            Assert.Equal(ResultPaneKind.Messages, tab.Panes.Selected!.Kind);
            Assert.True(view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MessagesPane").IsVisible);
        }
        finally
        {
            window.Close();
            view.DataContext = null;
        }
    }

    [AvaloniaFact]
    public void ManyResultSets_GetAPaneEach_UpToTheLimit_AndChoosingOneShowsIt()
    {
        var results = Enumerable.Range(0, 250).Select(index =>
        {
            var result = new QueryResult { TabLabel = $"Result {index + 1}", ResultIndex = index };
            result.ColumnInfos.Add(new ColumnInfo { Name = "n" });
            return result;
        }).ToArray();
        var tab = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            tab.SetExecutionResults(results, results[^1]);
            window.UpdateLayout();

            var resultPanes = tab.Panes.Items.Where(p => p.Kind == ResultPaneKind.Result).ToList();
            Assert.Equal(ResultTabSelection.Limit, resultPanes.Count);
            Assert.Same(results[^1], tab.Panes.Selected!.Result);

            tab.Panes.Selected = resultPanes[0];
            window.UpdateLayout();
            Assert.Same(resultPanes[0].Result, tab.SelectedResult);
            Assert.Same(resultPanes[0].Result, tab.CurrentResult);
            Assert.True(view.GetVisualDescendants().OfType<DockPanel>().Single(d => d.Name == "ResultPane").IsVisible);
        }
        finally
        {
            window.Close();
            view.DataContext = null;
        }
    }
}
