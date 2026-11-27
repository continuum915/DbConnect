using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class ExportHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothViewsRenderScopeProgressCancelAndOutcome(bool table)
    {
        var state = new ExportOperationViewModel();
        Control view;
        if (table)
        {
            var provider = new TableDataTestFixtures.TablePagingProvider(TableDataTestFixtures.CreatePage([]));
            var tab = new TableDataTabViewModel("Customers", TableDataTestFixtures.CreateSession(provider), TableDataTestFixtures.CreateTableNode(true));
            tab.ShowExportPanel = true;
            state = tab.ExportState;
            view = new TableDataTabView { DataContext = tab };
            Assert.Contains("excluding unsaved grid edits", tab.ExportScopeExplanation);
            Assert.Contains("no guaranteed snapshot", tab.ExportScopeExplanation);
        }
        else
        {
            var tab = new QueryTabViewModel { ShowExportOptionsPanel = true };
            state = tab.ExportState;
            view = new QueryTabView { DataContext = tab };
            Assert.Contains("row cap", tab.ExportScopeExplanation);
        }
        var picker = new PendingPicker();
        var task = state.RunAsync(picker, new LoadedExportSource([new("value", 0)], []), new(),
            table ? ExportScope.CurrentTablePage : ExportScope.DisplayedQueryResults, "test");
        Dispatcher.UIThread.RunJobs();
        if (table)
        {
            var scopes = view.FindControl<ComboBox>("ExportScopePicker")!;
            Assert.Equal(ExportScope.CurrentTablePage, scopes.SelectedItem);
            Assert.Contains(ExportScope.AllMatchingTableRows, scopes.Items.Cast<ExportScope>());
        }
        else
        {
            var scopes = view.FindControl<ComboBox>("QueryExportScopePicker")!;
            Assert.Equal(ExportScope.DisplayedQueryResults, scopes.SelectedItem);
            scopes.SelectedItem = ExportScope.FullQuery;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("unavailable", view.FindControl<TextBlock>("QueryExportScopeExplanation")!.Text);
            Assert.Contains(ExportScope.FullQuery, scopes.Items.Cast<ExportScope>());
        }
        var cancel = view.FindControl<Button>("CancelExportButton")!;
        Assert.True(cancel.IsVisible);
        Assert.True(view.FindControl<ProgressBar>("ExportProgress")!.IsVisible);
        cancel.Command!.Execute(null);
        picker.Result.SetResult(null);
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.False(cancel.IsVisible);
        Assert.Contains("Canceled", view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
        Assert.Contains("0 rows", view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
        var path = Path.Combine(Path.GetTempPath(), $"rain-headless-{Guid.NewGuid():N}.csv");
        try
        {
            await state.RunAsync(new FixedPicker(path), new LoadedExportSource([new("value", 0)], []), new(),
                table ? ExportScope.CurrentTablePage : ExportScope.DisplayedQueryResults, "test");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Completed", view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
            Assert.Contains(path, view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
            await state.RunAsync(new FixedPicker(Path.Combine(path, "missing", "file.csv")),
                new LoadedExportSource([new("value", 0)], []), new(), ExportScope.CurrentTablePage, "test");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Failed", view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
            Assert.Null(state.Outcome!.FinalPath);
        }
        finally { File.Delete(path); }
    }

    private sealed class PendingPicker : IExportPathPicker
    {
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Result.Task;
    }

    private sealed class FixedPicker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
