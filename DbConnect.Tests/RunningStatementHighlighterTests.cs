using System.Threading.Channels;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class RunningStatementHighlighterTests
{
    [Fact]
    public async Task LargeBatchPublishesLatestStatementAndIgnoresQueuedUpdatesAfterClear()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT 1;", 200));
        var tab = new QueryTabViewModel { QueryText = sql };
        var context = new QueuedContext();
        var highlighter = Create(tab, context);
        try
        {
            var first = highlighter.ShowAsync(0);
            (await context.TakeAsync())();
            await first;
            Assert.Equal(new StatementSpan(0, 8), tab.RunningStatementSpan);

            for (var i = 1; i < 200; i++)
                Assert.True(highlighter.ShowAsync(i).IsCompletedSuccessfully);
            // The timer callback reads the latest index, rather than capturing stale indices.
            (await context.TakeAsync())();
            Assert.Equal(new StatementSpan(199 * 9, 8), tab.RunningStatementSpan);

            await highlighter.ShowAsync(1);
            var queued = await context.TakeAsync();
            highlighter.Clear();
            queued();
            Assert.Null(tab.RunningStatementSpan);
            await highlighter.ShowAsync(2);
            Assert.Null(tab.RunningStatementSpan);
        }
        finally { highlighter.Clear(); }
    }

    [Fact]
    public async Task ClearBeforeInitialDispatchDoesNotRestoreHighlightOrLeaveCallerWaiting()
    {
        var tab = new QueryTabViewModel { QueryText = "SELECT 1; SELECT 2;" };
        var context = new QueuedContext();
        var highlighter = Create(tab, context);
        try
        {
            var first = highlighter.ShowAsync(0);
            highlighter.Clear();
            (await context.TakeAsync())();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(tab.RunningStatementSpan);
        }
        finally { highlighter.Clear(); }
    }

    private static RunningStatementHighlighter Create(QueryTabViewModel tab, SynchronizationContext context)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            return new RunningStatementHighlighter(tab, tab.QueryText, 0,
                SqlBatchDialect.Standard, throttleUpdates: true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();

        public override void Post(SendOrPostCallback callback, object? state)
            => _callbacks.Writer.TryWrite(() => callback(state));

        internal async Task<Action> TakeAsync()
            => await _callbacks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
