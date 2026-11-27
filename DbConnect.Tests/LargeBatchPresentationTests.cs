using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class LargeBatchPresentationTests
{
    private static QueryResult Affected() => new() { RecordsAffected = 1 };
    private static QueryResult Rows() => new() { ColumnInfos = [new ColumnInfo { Name = "value" }] };
    private static QueryResult Failed() => new() { ErrorMessage = "failed" };

    [Fact]
    public void BatchesWithinTheLimitKeepEveryResultTab()
    {
        var results = Enumerable.Range(0, ResultTabSelection.Limit).Select(_ => Affected()).ToArray();

        Assert.Equal(results, ResultTabSelection.Select(results));
    }

    [Fact]
    public void LargeBatchesKeepResultsWithRowsOrErrorsAndTheFinalResult()
    {
        var results = Enumerable.Range(0, ResultTabSelection.Limit + 50).Select(_ => Affected()).ToArray();
        var rows = results[3] = Rows();
        var failed = results[7] = Failed();

        Assert.Equal([rows, failed, results[^1]], ResultTabSelection.Select(results));
    }

    [Fact]
    public void LargeBatchesCapTabsAndKeepTheMostRecent()
    {
        var results = Enumerable.Range(0, ResultTabSelection.Limit * 3).Select(_ => Rows()).ToArray();

        Assert.Equal(results.TakeLast(ResultTabSelection.Limit), ResultTabSelection.Select(results));
    }

    [Fact]
    public void TabShowsNoTabStripAndExplainsWhenAffectedRowResultsAreHidden()
    {
        var tab = new QueryTabViewModel();
        var results = Enumerable.Range(0, 500).Select(_ => Affected()).ToArray();

        tab.SetExecutionResults(results, results[^1]);

        Assert.Equal(500, tab.Results.Count);
        Assert.True(tab.HasMultipleResults);
        Assert.Same(results[^1], tab.SelectedResult);
        Assert.Same(results[^1], Assert.Single(tab.ResultTabs));
        Assert.False(tab.ShowResultTabs);
        Assert.Contains("1 of 500", tab.ResultTabsSummary);
    }

    [Fact]
    public void TabKeepsEveryTabAndNoSummaryForAnOrdinaryBatch()
    {
        var tab = new QueryTabViewModel();
        QueryResult[] results = [Affected(), Rows()];

        tab.SetExecutionResults(results, results[1]);

        Assert.Equal(results, tab.ResultTabs);
        Assert.True(tab.ShowResultTabs);
        Assert.Equal(string.Empty, tab.ResultTabsSummary);
    }

    [Fact]
    public async Task LargeScriptHighlightNeverMakesTheWorkerWaitForTheUi()
    {
        var script = string.Concat(Enumerable.Range(0, 200).Select(i => $"SELECT {i};\n"));
        var tab = new QueryTabViewModel { QueryText = script };
        var ui = new HeldContext();
        var highlighter = CreateHighlighter(tab, script, ui);

        var waits = Enumerable.Range(0, 200).Select(highlighter.ShowAsync).ToArray();

        Assert.False(waits[0].IsCompleted);
        Assert.All(waits.Skip(1), wait => Assert.True(wait.IsCompleted));
        Assert.Null(tab.RunningStatementSpan);

        ui.RunAll();
        await waits[0];
        await ui.WaitForPostAsync();
        ui.RunAll();
        var span = Assert.IsType<StatementSpan>(tab.RunningStatementSpan);
        Assert.Equal("SELECT 199", script.Substring(span.Start, span.Length));
        Assert.Equal(2, ui.RunCount);
    }

    [Fact]
    public async Task LargeScriptHighlightCatchesUpToASlowStatementAndStaysClearedAfterRun()
    {
        var script = string.Concat(Enumerable.Range(0, 200).Select(i => $"SELECT {i};\n"));
        var tab = new QueryTabViewModel { QueryText = script };
        var ui = new HeldContext();
        var highlighter = CreateHighlighter(tab, script, ui);

        var first = highlighter.ShowAsync(0);
        ui.RunAll();
        await first;
        await highlighter.ShowAsync(5).WaitAsync(TimeSpan.FromSeconds(10));
        await ui.WaitForPostAsync();
        ui.RunAll();
        await highlighter.ShowAsync(6).WaitAsync(TimeSpan.FromSeconds(10));
        await ui.WaitForPostAsync();
        ui.RunAll();
        var span = Assert.IsType<StatementSpan>(tab.RunningStatementSpan);
        Assert.Equal("SELECT 6", script.Substring(span.Start, span.Length));

        await highlighter.ShowAsync(7).WaitAsync(TimeSpan.FromSeconds(10));
        await ui.WaitForPostAsync();
        highlighter.Clear();
        ui.RunAll();
        Assert.Null(tab.RunningStatementSpan);
    }

    [Fact]
    public void SmallScriptHighlightStillCompletesOnlyAfterTheUiShowsIt()
    {
        const string script = "SELECT 1;\nSELECT 2;";
        var tab = new QueryTabViewModel { QueryText = script };
        var ui = new HeldContext();
        var highlighter = CreateHighlighter(tab, script, ui);

        var wait = highlighter.ShowAsync(1);

        Assert.False(wait.IsCompleted);
        ui.RunAll();
        Assert.True(wait.IsCompleted);
        Assert.Equal(10, tab.RunningStatementSpan?.Start);
    }

    [Fact]
    public async Task ValuesSetWhileWorkspaceSettingsLoadAreNotOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rain-workspace-race-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "query-workspace.json");
        try
        {
            var workspace = new QueryWorkspaceService(path);
            await workspace.SaveTimeoutSecondsAsync(45);
            await workspace.SaveResultRowCapAsync(250);

            var untouched = new QueryTabViewModel("Untouched", new ObservableCollection<DatabaseSession>(), workspace);
            await WaitUntilAsync(() => untouched.TimeoutSeconds == 45 && untouched.ResultRowCap == 250);

            var tab = new QueryTabViewModel("Edited", new ObservableCollection<DatabaseSession>(), workspace);
            tab.TimeoutSeconds = 60;
            await WaitUntilAsync(() => tab.ResultRowCap == 250);

            Assert.Equal(60, tab.TimeoutSeconds);
        }
        finally
        {
            await new QueryWorkspaceService(path).LoadTimeoutSecondsAsync();
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(5, deadline.Token);
    }

    private static RunningStatementHighlighter CreateHighlighter(QueryTabViewModel tab, string script, SynchronizationContext ui)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try { return new RunningStatementHighlighter(tab, script, 0, SqlBatchDialect.Standard, throttleUpdates: true); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class HeldContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();
        private TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posts)
            {
                _posts.Enqueue((d, state));
                _posted.TrySetResult();
            }
        }

        public Task WaitForPostAsync()
        {
            lock (_posts) return _posted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void RunAll()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) post;
                lock (_posts)
                {
                    if (_posts.Count == 0)
                    {
                        _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        return;
                    }
                    post = _posts.Dequeue();
                }
                RunCount++;
                post.Callback(post.State);
            }
        }
    }
}
