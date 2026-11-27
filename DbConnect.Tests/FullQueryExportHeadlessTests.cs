using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class FullQueryExportHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullScopeShowsDisclosureAndRequiresDialogConfirmationBeforeRenderingOutcome(bool accept)
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "reporting",
            Provider = "SQLite",
            EnvironmentTier = "Production",
            ConnectionString = "Data Source=:memory:"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection })
        {
            ShowExportOptionsPanel = true
        };
        var session = await tab.GetOrOpenDbSessionAsync();
        var view = new QueryTabView { DataContext = tab };
        var owner = new Window { Content = view };
        var path = Path.Combine(Path.GetTempPath(), $"rain-full-headless-{Guid.NewGuid():N}.csv");
        owner.Show();
        try
        {
            tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT 1 AS value")]);
            tab.ExportScope = ExportScope.FullQuery;
            var picker = new Picker(path);
            tab.ExportPathPicker = picker;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Unsaved result edits", view.FindControl<TextBlock>("QueryExportScopeExplanation")!.Text);
            var task = tab.ExportCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows.OfType<ConfirmDialog>());
            var shown = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Production", shown);
            Assert.Contains("reporting", shown);
            Assert.Contains("Data may have changed", shown);
            Assert.Contains("costly", shown);
            Assert.Contains("client-only filtering/sorting", shown);
            Assert.Equal(0, picker.Calls);
            dialog.FindControl<Button>(accept ? "ConfirmButton" : "CancelButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await task;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(accept ? "Completed" : "Canceled", view.FindControl<TextBlock>("ExportOutcomeText")!.Text);
            Assert.Equal(accept ? 1 : 0, picker.Calls);
            Assert.Equal(accept, File.Exists(path));
        }
        finally
        {
            owner.Close();
            await tab.ReleaseDbSessionAsync();
            File.Delete(path);
        }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public int Calls { get; private set; }
        public Task<string?> PickSavePathAsync(string name, ExportFileType type)
        {
            Calls++;
            return Task.FromResult<string?>(path);
        }
    }
}
