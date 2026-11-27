using System;
using System.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// When a driver failure counts as the user's cancel. The rule must never hide a
/// genuine error behind "cancelled".
/// </summary>
public sealed class QueryCancellationTests
{
    private sealed class DriverError : Exception;

    private static QueryExecutionOptions Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return new QueryExecutionOptions { CancellationToken = source.Token };
    }

    private static readonly QueryExecutionOptions Live = new() { CancellationToken = new CancellationTokenSource().Token };

    [Fact]
    public void OperationCanceled_IsACancellationWithOrWithoutOptions()
    {
        Assert.True(QueryCancellation.IsCancellation(new OperationCanceledException(), null));
        Assert.True(QueryCancellation.IsCancellation(new OperationCanceledException(), Live));
    }

    [Fact]
    public void DriverError_WhileTheCallerHasNotCancelled_IsARealError()
    {
        Assert.False(QueryCancellation.IsCancellation(new DriverError(), Live, _ => true));
        Assert.False(QueryCancellation.IsCancellation(new DriverError(), null, _ => true));
    }

    [Fact]
    public void DriverError_ThatIsNotAnInterruption_IsARealErrorEvenAfterACancel()
    {
        // e.g. a syntax or constraint error that raced with the cancel.
        Assert.False(QueryCancellation.IsCancellation(new DriverError(), Cancelled(), _ => false));
    }

    [Fact]
    public void DriverError_WithoutAnInterruptionRule_IsARealErrorEvenAfterACancel()
    {
        Assert.False(QueryCancellation.IsCancellation(new DriverError(), Cancelled()));
    }

    [Fact]
    public void DriverInterruption_AfterACancel_IsACancellation()
    {
        Assert.True(QueryCancellation.IsCancellation(new DriverError(), Cancelled(), _ => true));
    }

    [Fact]
    public void CancelledError_NamesTheProviderAndIsMarkedCancelled()
    {
        var error = QueryCancellation.CancelledError("SQL Server");

        Assert.Equal("SQL Server", error.Provider);
        Assert.True(error.IsCanceled);
        Assert.False(error.IsTimeout);
    }
}
