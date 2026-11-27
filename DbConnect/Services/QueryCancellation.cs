using System;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Decides when a failure is the user's own cancel rather than a genuine error,
/// so a real failure is never hidden behind "cancelled".
/// </summary>
public static class QueryCancellation
{
    /// <summary>
    /// True for <see cref="OperationCanceledException"/>. A driver-specific exception counts too, but
    /// only when the caller actually cancelled AND <paramref name="isDriverInterruption"/> recognises
    /// it as the driver's way of reporting an interrupted command. Without that rule, anything else
    /// stays a real error even if it raced with a cancel.
    /// </summary>
    public static bool IsCancellation(
        Exception exception,
        QueryExecutionOptions? options,
        Func<Exception, bool>? isDriverInterruption = null) =>
        exception is OperationCanceledException ||
        (options?.CancellationToken.IsCancellationRequested == true &&
         isDriverInterruption?.Invoke(exception) == true);

    public static QueryErrorInfo CancelledError(string providerName) => new()
    {
        Provider = providerName,
        Summary = "The query was cancelled.",
        Detail = "DbConnect cancelled the statement before it completed.",
        IsCanceled = true
    };
}
