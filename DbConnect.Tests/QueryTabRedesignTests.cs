using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>UI redesign 06: the query tab's toolbar, result strip, Messages grid and footer.</summary>
public class QueryTabRedesignTests
{
    [AvaloniaFact]
    public void Stop_IsDisabledWhileIdle_AndEnabledWhileRunning()
    {
        var (tab, view, window) = Show();
        try
        {
            var stop = Named<Button>(view, "StopButton");
            Assert.False(stop.IsEnabled);

            tab.IsExecuting = true;
            window.UpdateLayout();
            Assert.True(stop.IsEnabled);
        }
        finally
        {
            tab.IsExecuting = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AResultSet_GetsAResultPane_AndTheFooterShowsTimeAndRows()
    {
        var (tab, view, window) = Show();
        try
        {
            var result = new QueryResult { ExecutedSql = "SELECT id\nFROM orders", Duration = TimeSpan.FromMilliseconds(142) };
            result.ColumnInfos.Add(new ColumnInfo { Name = "id" });
            result.ObjectRows.Add([1]);
            result.ObjectRows.Add([2]);
            tab.SetExecutionResults([result], result);
            window.UpdateLayout();

            Assert.Equal(["Messages", "Result", "Profile", "Status", "Explain", "Chart"], tab.Panes.Items.Select(p => p.Header));
            Assert.Equal(ResultPaneKind.Result, tab.Panes.Selected!.Kind);
            Assert.Equal("Query time: 0.142 s", Named<TextBlock>(view, "FooterTime").Text);
            Assert.Equal("2 rows · all fetched", Named<TextBlock>(view, "FooterRows").Text);
            Assert.Equal("SELECT id FROM orders", Named<TextBlock>(view, "ResultSummaryText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AFailingStatement_ShowsItsErrorInTheMessagesGrid()
    {
        var (tab, view, window) = Show();
        try
        {
            var failed = new QueryResult
            {
                Error = QueryErrorInfo.Failure("SQLite", "no such table: nope", "no such table: nope"),
                Statements = [new QueryStatementExecution { Index = 0, Sql = "SELECT * FROM nope", Error = QueryErrorInfo.Failure("SQLite", "no such table: nope", "no such table: nope") }],
            };
            tab.SetExecutionResults([failed], failed);
            window.UpdateLayout();

            Assert.Equal(ResultPaneKind.Messages, tab.Panes.Selected!.Kind);
            var row = Assert.Single(tab.MessagesRows);
            Assert.Equal("SELECT * FROM nope", row.Statement);
            Assert.True(row.HasError);
            Assert.Contains("no such table", row.Result);
            Assert.True(Named<Grid>(view, "MessagesPane").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AutoCommit_IsTheInverseOfManualCommit_AndCommitLightsUpInATransaction()
    {
        var (tab, view, window) = Show();
        try
        {
            var autoCommit = Named<CheckBox>(view, "AutoCommit");
            tab.IsManualCommit = false; // new tabs follow the Preferences default
            window.UpdateLayout();
            Assert.True(autoCommit.IsChecked);

            var point = autoCommit.TranslatePoint(new Avalonia.Point(8, autoCommit.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Assert.False(autoCommit.IsChecked);
            Assert.True(tab.IsManualCommit);

            tab.IsInTransaction = true;
            window.UpdateLayout();
            Assert.Contains("active", Named<Button>(view, "CommitButton").Classes);
            Assert.Contains("active", Named<Button>(view, "RollbackButton").Classes);
        }
        finally
        {
            tab.IsInTransaction = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Explain_ShowsTheExplainPane()
    {
        var (tab, view, window) = Show();
        try
        {
            tab.ShowExplainPane = true;
            window.UpdateLayout();
            Assert.Equal(ResultPaneKind.Explain, tab.Panes.Selected!.Kind);
            Assert.True(Named<DockPanel>(view, "ExplainPane").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static (QueryTabViewModel Tab, QueryTabView View, Window Window) Show()
    {
        var tab = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        window.UpdateLayout();
        return (tab, view, window);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
