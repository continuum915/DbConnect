using System.Text;

namespace DbConnect.PerformanceTests;

public static class EditorReleaseReport
{
    public static void Append(StringBuilder markdown, IEnumerable<RunRecord> records)
    {
        var runs = records.Where(x => x.Editor?.Release != null).ToArray();
        if (runs.Length == 0) return;
        markdown.AppendLine("\n## Editor release after close\n");
        markdown.AppendLine("Open and post-close diagnostics force full collections outside the measured close operation. The same fixture and validation oracle remain alive in both phases. The open editor has edit/undo history (redo available); retained history is expected while open. Closing the headless window, detaching its content and model, and releasing its session are measured programmatic cleanup, not a MainWindow tab-close or native display measurement. Bounded dispatcher observation does not prove highlighting is complete.\n");
        markdown.AppendLine("| Bytes / repetition / outcome | Phase | Managed / private / working set bytes | Weak targets alive: editor / view / document / snapshot / model | Close sync / deferred ms | Dispatcher samples / missed |");
        markdown.AppendLine("|---|---|---|---|---|---|");
        foreach (var run in runs)
        {
            var release = run.Editor!.Release!;
            foreach (var (name, memory) in new[] { ("open", release.Open), ("closed", release.Closed) }.Where(x => x.Item2 != null))
            {
                var sample = memory!;
                markdown.AppendLine(FormattableString.Invariant($"| {run.Settings.SizeBytes} / {run.Repetition} / {run.Outcome} | {name} | {sample.ManagedBytes} / {sample.PrivateBytes} / {sample.WorkingSetBytes} | {sample.EditorAlive} / {sample.ViewAlive} / {sample.DocumentAlive} / {sample.SnapshotAlive} / {sample.ModelAlive} | {release.CloseSynchronousMilliseconds:0.###} / {release.DeferredMilliseconds:0.###} | {release.Dispatcher.SampleCount} / {release.Dispatcher.MissedIntervals} |"));
            }
        }
        markdown.AppendLine("\nWeak references indicate only whether these targets survived the diagnostic collections; they do not identify roots or prove all related objects were released. The UI dispatcher, framework caches, finalizers and diagnostic timing can retain objects. Managed heap is process-wide; private bytes and working set include runtime, native and shared effects and need not fall after collection. Parent memory samples cover the whole process lifetime. Fixture/oracle memory remains intentionally retained; no retained-byte attribution is inferred from a difference.\n");
    }
}
