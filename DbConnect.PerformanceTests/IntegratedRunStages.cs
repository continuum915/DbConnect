using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.PerformanceTests;

public sealed record IntegratedRunStage(string Name, double? Milliseconds);

/// <summary>
/// Publishes Run stage boundaries as supervised phases, without production timing hooks. Phases
/// survive a timeout, so a stalled run still shows the stage it stopped in.
/// </summary>
public static class IntegratedRunStages
{
    public const string RunStarted = "run-started";
    public const string ExecutionStarted = "execution-started";
    public const string FirstSubmission = "first-submission";
    public const string LastSubmission = "last-submission";
    public const string ResultsAssigned = "results-assigned";
    public const string ExecutionFinished = "execution-finished";
    public const string CommandReturned = "command-returned";
    public const string RenderOpportunity = "render-opportunity";

    private static readonly (string Name, string Start, string End)[] Stages =
    [
        ("Before execution starts (UI thread)", RunStarted, ExecutionStarted),
        ("Preparation and session (worker)", ExecutionStarted, FirstSubmission),
        ("Submission, including UI round trips", FirstSubmission, LastSubmission),
        ("Executor completion", LastSubmission, ResultsAssigned),
        ("Presentation and history", ResultsAssigned, ExecutionFinished),
        ("Command return to render", CommandReturned, RenderOpportunity),
        ("Total, Run start to render", RunStarted, RenderOpportunity)
    ];

    public static IReadOnlyList<string> StageNames { get; } = Stages.Select(stage => stage.Name).ToArray();

    /// <summary>A stage the run never finished has no duration.</summary>
    public static IReadOnlyList<IntegratedRunStage> From(IReadOnlyList<PhaseTiming> phases)
    {
        double? At(string phase) => phases.FirstOrDefault(item => item.Phase == phase)?.ChildElapsedMilliseconds;
        return Stages.Select(stage => new IntegratedRunStage(stage.Name, At(stage.End) - At(stage.Start))).ToArray();
    }

    // Subscribe before the view exists: handlers run in order, so these fire ahead of synchronous binding work.
    internal static void Observe(QueryTabViewModel tab, Action<string> send)
    {
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(QueryTabViewModel.Results) && tab.IsExecuting)
                send(ResultsAssigned);
            else if (args.PropertyName == nameof(QueryTabViewModel.IsExecuting))
                send(tab.IsExecuting ? ExecutionStarted : ExecutionFinished);
        }
        tab.PropertyChanged += Changed;
    }

    /// <summary>Opens the Messages tab the result summary points to, checks it lists every statement, and times the first render.</summary>
    internal static double MeasureMessagesTab(Window window, QueryTabView view, QueryTabViewModel tab, int expectedStatements)
    {
        var messages = view.GetLogicalDescendants().OfType<TabItem>().First(item => item.Header as string == "Messages");
        var start = Stopwatch.GetTimestamp();
        messages.IsSelected = true;
        window.UpdateLayout();
        using (window.CaptureRenderedFrame()) { }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var grid = messages.GetLogicalDescendants().OfType<DataGrid>().First();
        if (grid.ItemsSource?.Cast<object>().Count() != expectedStatements)
            throw new InvalidDataException("Messages tab does not list every statement.");
        return elapsed;
    }

    internal static void ObserveSubmissions(ImmediateDbSession fake, int expectedCommands, Action<string> send) =>
        fake.CommandSubmitted = count =>
        {
            if (count == 1) send(FirstSubmission);
            if (count == expectedCommands) send(LastSubmission);
            else if (count == expectedCommands / 4) send("submitted-25-percent");
            else if (count == expectedCommands / 2) send("submitted-50-percent");
            else if (count == expectedCommands / 4 * 3) send("submitted-75-percent");
        };
}
