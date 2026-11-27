using System.Diagnostics;
using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record ExecutionStopObservations(string ExpectedOutcome, string ObservedOutcome,
    int StopCommandNumber, int SubmittedCommands, int ResultCount, string? ErrorCode,
    bool IsCanceled, double? CancellationMilliseconds, bool NoLaterSubmissions,
    bool SessionDisposed, int ActiveCommandsAfterCleanup);

public static class ExecutionStopChild
{
    public const string CleanupHangId = "harness.stop-cleanup-hang";
    public const string CleanupFailureId = "harness.stop-cleanup-failure";
    public const string CancelId = "execute.cancel";
    public const string ErrorId = "execute.error";
    public const string Sql = "SELECT 101\r\nGO\r\nSELECT 202\r\nGO\r\nSELECT 303";
    public static bool IsStop(string id) => id is ErrorId or CancelId or CleanupHangId or CleanupFailureId;

    public static async Task<(QueryResult DisplayResult, IReadOnlyList<QueryResult> Results)> ExecuteScriptAsync(
        IDbSession session, CancellationToken cancellationToken)
    {
        var result = await QueryBatchExecutor.ExecuteAsync(new DatabaseSession(new ConnectionInfo(), session.Provider),
            session, Sql, QueryExecutionScope.FullEditor, 30, 100, _ => Task.CompletedTask, cancellationToken);
        return (result.DisplayResult, result.Results);
    }
    public static async Task<int> RunAsync(RunSettings settings)
    {
        var lifetime = Stopwatch.StartNew();
        void Send(string phase, OperationMetrics? metrics = null, ExecutionStopObservations? stop = null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new ChildMessage(phase, metrics,
                lifetime.Elapsed.TotalMilliseconds, Stop: stop)));
            Console.Out.Flush();
        }
        Send("initialized");
        var cancel = settings.ScenarioId != ErrorId;
        using var cancellation = new CancellationTokenSource();
        var cleanupFault = settings.ScenarioId switch
        {
            CleanupHangId => StopCleanupFault.Hang,
            CleanupFailureId => StopCleanupFault.Throw,
            _ => StopCleanupFault.None
        };
        var fake = new StopDbSession(cancel, cleanupFault);
        Send("fixture-ready");
        if (await Console.In.ReadLineAsync() != "go") { await fake.DisposeAsync(); return 24; }
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        (QueryResult DisplayResult, IReadOnlyList<QueryResult> Results) result;
        Task<(QueryResult DisplayResult, IReadOnlyList<QueryResult> Results)>? operation = null;
        double? cancellationMs = null;
        try
        {
            operation = ExecuteScriptAsync(fake, cancellation.Token);
            if (cancel)
            {
                if (await Task.WhenAny(fake.Blocked, operation) != fake.Blocked)
                {
                    await operation; // Surface an unexpected executor failure before the gate.
                    throw new InvalidDataException("Executor completed before the second command blocked.");
                }
                Send("command-blocked");
                Send("cancellation-requested");
                var requestedAt = Stopwatch.GetTimestamp();
                cancellation.Cancel();
                result = await operation;
                cancellationMs = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
            }
            else result = await operation;
            Send("execution-returned");
        }
        finally
        {
            Send("cleanup-started");
            try
            {
                cancellation.Cancel();
                fake.ReleaseBlockedCommand();
                if (operation != null) await operation;
            }
            finally { await fake.DisposeAsync(); }
            Send("cleanup-completed");
        }
        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
        var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        var heap = GC.GetTotalMemory(false);
        // Literal oracle and all correctness checks are outside operation counters.
        if (!fake.SubmittedCommands.SequenceEqual(new[] { "SELECT 101", "SELECT 202" }) ||
            result.Results.Count != 2 || result.Results[0].HasError || result.Results[0].RecordsAffected != 1 ||
            !ReferenceEquals(result.DisplayResult, result.Results[1]) ||
            result.DisplayResult.Error == null || !result.DisplayResult.HasError ||
            (cancel ? !result.DisplayResult.Error.IsCanceled : result.DisplayResult.Error.Code != StopDbSession.ErrorCode) ||
            result.DisplayResult.StatementCount != 2 ||
            !fake.Disposed || fake.ActiveCommands != 0)
            throw new InvalidDataException("Stop scenario did not stop at the expected command/result or release resources.");
        var outcome = cancel ? "canceled" : "error";
        var observed = new ExecutionStopObservations(outcome, result.DisplayResult.Error.IsCanceled ? "canceled" : "error", 2, fake.SubmittedCommands.Count,
            result.Results.Count, result.DisplayResult.Error.Code, result.DisplayResult.Error.IsCanceled, cancellationMs, true, fake.Disposed, fake.ActiveCommands);
        Send("validated", stop: observed);
        Send("complete", new(duration, allocation, deltas, heap, GC.GetTotalMemory(true)), observed);
        return 0;
    }
}
