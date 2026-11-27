using System.Globalization;
using System.Text;

namespace DbConnect.PerformanceTests;

public static class IntegratedRunReport
{
    public static void Append(StringBuilder markdown, IReadOnlyList<RunRecord> records)
    {
        markdown.AppendLine("\n## Integrated Run and scheduled editor recovery\n");
        markdown.AppendLine("execute.integrated invokes the normal MainWindowViewModel Run command through parameter detection, running highlighter preparation, worker executor orchestration and view-model result presentation. The real QueryTabView renders the presented state; headless rendering is not native display throughput. editor.paste-recovery measures real clipboard paste with the normal workspace recovery timer. Both use a fresh empty parent-assigned profile verified before app initialization and a fail-closed fake provider; no live connections or orphan restoration. The tab has no history store.\n");
        markdown.AppendLine("Operation envelope includes command plus deferred observation. Command milliseconds exclude observation. Recovery debounce is 2000 ms, followed by normal serialization/write; recovery observation adds 2500 ms after the bounded 300 ms render observation and dispatcher drain. Persisted content is checked outside timing. Dispatcher samples cover command/render observation only, not the additional recovery wait. TextMate completion remains unknown. Fake results are affected-row metadata only; retention fields in integrated execution evidence are unmeasured (zero).\n");
        markdown.AppendLine("| Scenario / bytes / repetition | Command ms | Observation ms | Commands / presented results | Highlight updates | Persisted bytes / verified | Outcome |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---|---|");
        foreach (var run in records.Where(x => IsIntegrated(x)))
        {
            var observed = run.Editor?.Integrated;
            string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
            markdown.AppendLine($"| {run.Settings.ScenarioId} / {run.Settings.SizeBytes} / {run.Repetition}{(run.Warmup ? " warm-up" : "")} | {F(observed?.CommandMilliseconds)} | {F(observed?.ObservationMilliseconds)} | {observed?.Execution?.CommandCount} / {observed?.Execution?.ResultCount} | {observed?.HighlightUpdates} | {observed?.PersistedBytes} / {observed?.SnapshotVerified} | {run.Outcome} |");
        }
        markdown.AppendLine("\n### Integrated Run stage timings\n");
        markdown.AppendLine("Stage intervals are elapsed wall time relative to command start. Dispatcher columns describe stage entry/exit, not continuous thread occupancy. execute-batch includes worker parsing/execution and awaited statement highlighting; render-queue-drain includes pending dispatcher work. layout-render measures explicit layout and frame capture after that drain. Observation includes the separate 300 ms window; it must not be attributed to presentation. Missing stages in older records are unmeasured. No SQL or values are recorded.\n");
        markdown.AppendLine("GC pause and allocation deltas are process-wide counters across each stage, including concurrent work; they are not exclusive per-stage attribution. Dispatcher samples include due, enqueue, and completion timestamps. Cadence delay includes producer lateness, so process-wide GC pauses before enqueueing are visible; queue delay alone can miss them. GC pause since the previous sample is correlation evidence, not an exact individual-GC start/end trace.\n");
        markdown.AppendLine("| Bytes / repetition | Stage | Start ms | Duration ms | GC pause ms | Allocated bytes | Dispatcher entry / exit |");
        markdown.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var run in records.Where(run => run.Settings.ScenarioId == IntegratedRunScenario.RunId))
        {
            var label = $"{run.Settings.SizeBytes} / {run.Repetition}{(run.Warmup ? " warm-up" : "")}";
            if (run.Editor?.Integrated?.Stages is not { Length: > 0 } stages)
            {
                markdown.AppendLine($"| {label} | unmeasured | — | — | — | — | — |");
                continue;
            }
            foreach (var stage in stages)
                markdown.AppendLine(FormattableString.Invariant(
                    $"| {label} | {stage.Stage} | {stage.StartMilliseconds:0.###} | {stage.DurationMilliseconds:0.###} | {stage.GcPauseMilliseconds:0.###} | {stage.AllocatedBytes} | {stage.StartedOnDispatcher} / {stage.EndedOnDispatcher} |"));
        }
        markdown.AppendLine("\n### Largest dispatcher cadence gap\n");
        markdown.AppendLine("| Bytes / repetition | Due ms | Enqueued ms | Completed ms | Queue delay ms | Cadence delay ms | GC pause since previous sample ms |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var run in records.Where(run => run.Settings.ScenarioId == IntegratedRunScenario.RunId))
        {
            var sample = run.Editor?.Dispatcher.Samples?.MaxBy(sample => sample.CadenceDelayMilliseconds);
            if (sample == null) continue;
            markdown.AppendLine(FormattableString.Invariant(
                $"| {run.Settings.SizeBytes} / {run.Repetition} | {sample.DueMilliseconds:0.###} | {sample.EnqueuedMilliseconds:0.###} | {sample.CompletedMilliseconds:0.###} | {sample.DelayMilliseconds:0.###} | {sample.CadenceDelayMilliseconds:0.###} | {sample.GcPauseSincePreviousSampleMilliseconds:0.###} |"));
        }
        SetupMemoryReport.Append(markdown, records);
    }

    private static bool IsIntegrated(RunRecord run) => IntegratedRunScenario.IsIntegrated(run.Settings.ScenarioId);
}
