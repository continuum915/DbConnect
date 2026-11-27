using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 06: tool tabs show readable grid headers, empty states with a next step and no duplicated labels.</summary>
public sealed class ToolTabLayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-tool-tabs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [AvaloniaFact]
    public void ToolGridHeaders_AreNotTrimmed()
    {
        var diff = new SchemaDiffView { DataContext = new SchemaDiffViewModel() };
        using (ThemedHost.Show(diff, "Light", height: 700, width: 1100))
            ObjectTabLayoutTests.AssertHeadersFit(diff.FindControl<DataGrid>("DiffGrid")!, ["Include", "Type", "Name", "Status", "Notes"]);

        var automation = new AutomationView { DataContext = NewAutomation() };
        using (ThemedHost.Show(automation, "Light", height: 700, width: 1100))
            ObjectTabLayoutTests.AssertHeadersFit(automation.FindControl<DataGrid>("RunHistoryGrid")!, ["Started", "Status", "Rows", "Error"]);
    }

    [AvaloniaFact]
    public void AutomationWithNoJobs_OffersANewJob()
    {
        var viewModel = NewAutomation();
        var view = new AutomationView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);

        var empty = VisibleEmptyStates(view).Single(e => e.Title == "No jobs yet");
        Assert.Same(viewModel.NewJobCommand, empty.ActionCommand);
        Assert.Equal("New job", empty.ActionText);
    }

    [AvaloniaFact]
    public void ConnectionMonitorForAnEmbeddedProvider_ShowsItsMessageOnce()
    {
        const string message = "This provider has no active server connections to monitor.";
        var viewModel = new ConnectionMonitorViewModel { IsEmbeddedProvider = true, StatusMessage = message };
        var view = new ConnectionMonitorView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);

        Assert.Single(VisibleTexts(view), message);
    }

    [AvaloniaFact]
    public void QueryBuilder_HasOneOpenInEditorAndCallsItsFormDesignView()
    {
        var view = new QueryBuilderView { DataContext = new QueryBuilderViewModel() };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);

        var texts = VisibleTexts(view).ToList();
        Assert.Single(texts, "Open in Editor");
        Assert.Contains("Design View", texts);
        Assert.DoesNotContain("Form View", texts);
    }

    private AutomationViewModel NewAutomation() =>
        new(new ObservableCollection<DatabaseSession>(), new AutomationService(dataDirectory: _directory));

    private static IEnumerable<EmptyState> VisibleEmptyStates(Control root) =>
        root.GetVisualDescendants().OfType<EmptyState>().Where(e => e.IsEffectivelyVisible);

    private static IEnumerable<string> VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!);
}
