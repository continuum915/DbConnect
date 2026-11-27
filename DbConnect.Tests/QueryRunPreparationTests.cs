using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class QueryRunPreparationTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("resolve-source")]
    [InlineData("detect-parameters")]
    [InlineData("substitute-parameters")]
    [InlineData("prepare-highlighter")]
    public async Task CancelDuringPreparationLeavesNoSessionAndAllowsAnotherRun(string stage)
    {
        var owner = CreateMainWindowViewModel();
        owner.Tabs.Clear();
        var tab = new QueryTabViewModel { QueryText = "SELECT @value;" };
        tab.SelectedSession = new DatabaseSession(new ConnectionInfo { ConnectionString = "Data Source=:memory:" }, new SqliteProvider());
        owner.Tabs.Add(tab);
        owner.SelectedTab = tab;
        owner.ShowQueryParamDialogFunc = dialog =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.True(tab.IsExecuting);
            dialog.Parameters[0].Value = 7;
            dialog.Parameters[0].DataType = "int";
            return Task.FromResult<QueryParamDialogResult?>(dialog.GetResult(true));
        };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueryRunDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != stage || entered.Task.IsCompleted) return;
                entered.TrySetResult(Dispatcher.UIThread.CheckAccess());
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Preparation test gate was not released.");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var run = owner.ExecuteQueryCommand.ExecuteAsync(null);
        try
        {
            Assert.False(await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(owner.IsExecuting);
            Assert.True(tab.IsExecuting);
            Assert.False(tab.HasDbSession);
            tab.CancelExecutionCommand.Execute(null);
        }
        finally { release.Set(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tab.CurrentResult?.Error?.IsCanceled);
        Assert.False(owner.IsExecuting);
        Assert.False(tab.IsExecuting);
        Assert.False(tab.HasDbSession);
        Assert.Null(tab.RunningStatementSpan);

        tab.QueryText = "SELECT 7;";
        await owner.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.False(tab.CurrentResult!.HasError);
        Assert.True(tab.HasDbSession);
    }

    [AvaloniaFact]
    public async Task DismissingParameterDialogCleansUpPreparationStateWithoutOpeningSession()
    {
        var owner = CreateMainWindowViewModel();
        owner.Tabs.Clear();
        var tab = new QueryTabViewModel { QueryText = "SELECT @value;" };
        tab.SelectedSession = new DatabaseSession(new ConnectionInfo { ConnectionString = "Data Source=:memory:" }, new SqliteProvider());
        owner.Tabs.Add(tab);
        owner.SelectedTab = tab;
        owner.ShowQueryParamDialogFunc = dialog => Task.FromResult<QueryParamDialogResult?>(dialog.GetResult(false));

        await owner.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.False(owner.IsExecuting);
        Assert.False(tab.IsExecuting);
        Assert.False(tab.HasDbSession);
        Assert.Null(tab.RunningStatementSpan);
    }

    [AvaloniaFact]
    public async Task EditingDuringPreparationDoesNotChangeSubmittedSnapshotOrHighlightNewText()
    {
        var owner = CreateMainWindowViewModel();
        owner.Tabs.Clear();
        var tab = new QueryTabViewModel { QueryText = "SELECT 7;" };
        tab.SelectedSession = new DatabaseSession(new ConnectionInfo { ConnectionString = "Data Source=:memory:" }, new SqliteProvider());
        owner.Tabs.Add(tab);
        owner.SelectedTab = tab;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueryRunDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "detect-parameters") return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Snapshot test gate was not released.");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var highlights = 0;
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tab.RunningStatementSpan) && tab.RunningStatementSpan != null) highlights++;
        };
        var run = owner.ExecuteQueryCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            tab.QueryText = "SELECT missing;";
        }
        finally { release.Set(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(tab.CurrentResult!.HasError);
        Assert.Equal("SELECT 7", tab.CurrentResult.ExecutedSql);
        Assert.Equal("SELECT missing;", tab.QueryText);
        Assert.Equal(0, highlights);
        Assert.False(tab.IsExecuting);
    }

    [AvaloniaFact]
    public async Task SubstitutedSelectionParsesSubmittedTextInsteadOfReusingOriginalSpans()
    {
        var owner = CreateMainWindowViewModel();
        owner.Tabs.Clear();
        var tab = new QueryTabViewModel { QueryText = "SELECT 99;\nSELECT @value;\nSELECT @value;" };
        tab.SelectedSession = new DatabaseSession(new ConnectionInfo { ConnectionString = "Data Source=:memory:" }, new SqliteProvider());
        tab.SelectedQueryStart = tab.QueryText.IndexOf("SELECT @value", StringComparison.Ordinal);
        tab.SelectedQueryText = tab.QueryText[tab.SelectedQueryStart..];
        owner.Tabs.Add(tab);
        owner.SelectedTab = tab;
        owner.ShowQueryParamDialogFunc = dialog =>
        {
            dialog.Parameters[0].Value = "a much longer;literal";
            return Task.FromResult<QueryParamDialogResult?>(dialog.GetResult(true));
        };

        await owner.ExecuteSelectionCommand.ExecuteAsync(null);

        Assert.Equal(2, tab.Results.Count);
        Assert.All(tab.Results, result =>
        {
            Assert.False(result.HasError);
            Assert.Equal("SELECT 'a much longer;literal'", result.ExecutedSql);
        });
    }
}
