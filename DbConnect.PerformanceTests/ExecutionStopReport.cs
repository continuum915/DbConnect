using System.Globalization;
using System.Text;

namespace DbConnect.PerformanceTests;

public static class ExecutionStopReport
{
    public static void Append(StringBuilder markdown, IReadOnlyList<RunRecord> records)
    {
        var runs = records.Where(run => ExecutionStopChild.IsStop(run.Settings.ScenarioId)).ToArray();
        if (runs.Length == 0) return;
        markdown.AppendLine("\n## Execution stop correctness\n");
        markdown.AppendLine("Fixed three-command SELECT script; deterministic failure/cancellation at command two. These are DbConnect orchestration checks, not real-provider/server interruption guarantees or immediate-fake throughput measurements. No batch semantics change. Completed means the expected error/cancellation and absence of later submissions were verified, not that all SQL succeeded.\n");
        markdown.AppendLine("Cancellation is requested only after the second fake command signals it is blocked. Latency uses Stopwatch from token cancellation request to executor return; it excludes validation and cleanup. The operation envelope includes executor work, phase instrumentation and owned cleanup. The fake consumes the execution-options cancellation token and returns a structured canceled result; it does not simulate a server or prove IDbSession.CancelAsync driver interruption. No database connections, profile persistence or large fixtures are used.\n");
        markdown.AppendLine("| Scenario / repetition | Run outcome / last phase | Expected / observed stop | Submitted / results | Error code / canceled | Cancel ms | No later submissions / disposed / active |\n|---|---|---|---:|---|---:|---|");
        foreach (var run in runs)
        {
            var stop = run.Stop;
            var latency = stop?.CancellationMilliseconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
            markdown.AppendLine($"| {run.Settings.ScenarioId} / {run.Repetition}{(run.Warmup ? " warm-up" : "")} | {run.Outcome} / {run.LastCompletedPhase} | {stop?.ExpectedOutcome ?? "unavailable"} / {stop?.ObservedOutcome ?? "unavailable"} | {stop?.SubmittedCommands} / {stop?.ResultCount} | {stop?.ErrorCode} / {stop?.IsCanceled} | {latency} | {stop?.NoLaterSubmissions} / {stop?.SessionDisposed} / {stop?.ActiveCommandsAfterCleanup} |");
        }
        markdown.AppendLine("\nThe harness.stop-cleanup-* IDs deliberately hang or throw during disposal after releasing the owned gate. Parent timeout/crash records retain phase timestamps, exit status, memory samples and failure details; unsuccessful runs have no successful operation metrics.\n");
    }
}
