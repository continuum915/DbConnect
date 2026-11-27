using DbConnect.Models;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class ExecutionStopExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorReturnsStopResultAndNeverSubmitsTheFollowingCommand(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new StopDbSession(cancel);
        Task<(QueryResult DisplayResult, IReadOnlyList<QueryResult> Results)>? operation = null;
        try
        {
            operation = ExecutionStopChild.ExecuteScriptAsync(fake, cancellation.Token);
            if (cancel)
            {
                await fake.Blocked.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(operation.IsCompleted);
                cancellation.Cancel();
            }
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "SELECT 101", "SELECT 202" }, fake.SubmittedCommands);
            Assert.Equal(2, result.Results.Count);
            Assert.False(result.Results[0].HasError);
            Assert.Equal(1, result.Results[0].RecordsAffected);
            Assert.Same(result.Results[1], result.DisplayResult);
            Assert.True(result.DisplayResult.HasError);
            Assert.Equal(cancel, result.DisplayResult.Error!.IsCanceled);
            if (!cancel) Assert.Equal("DBCONNECT_FAKE_ERROR", result.DisplayResult.Error.Code);
            Assert.Equal(2, result.DisplayResult.StatementCount);
            Assert.Equal(1, result.DisplayResult.ReturnedStatementIndex);
            Assert.Equal("SELECT 202", result.DisplayResult.ExecutedSql);
        }
        finally
        {
            cancellation.Cancel();
            fake.ReleaseBlockedCommand();
            try { if (operation != null) await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await fake.DisposeAsync(); }
        }
        Assert.True(fake.Disposed);
        Assert.Equal(0, fake.ActiveCommands);
    }
}
