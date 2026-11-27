using System.Globalization;
using System.Text;

namespace DbConnect.PerformanceTests;

public static class EditorSelectionReport
{
    public static void Append(StringBuilder markdown, IEnumerable<RunRecord> records)
    {
        var runs = records.Where(run => run.Editor?.Operations != null && run.Editor.Navigation == null).ToArray();
        if (runs.Length == 0) return;
        markdown.AppendLine("\n## Selection, replacement, undo and redo\n");
        markdown.AppendLine("Each fresh child starts with the representative fixture, selects its middle half, replaces it with SELECT 2;, then undoes and redoes. UI warm-up and a 300 ms pre-operation pause are excluded. Selection is programmatic; replacement is headless text input; undo/redo are editor command APIs. Each operation has a separate dispatcher probe, render opportunity and bounded 300 ms deferred observation. Version age is relative to the preceding state (0 unchanged, 1 newer). Undo restores the large selection; redo selects the replacement. Exact document/model text, caret, selection and version checks run outside timing and materialize model text between operations.\n");
        markdown.AppendLine("| Profile / bytes / repetition / warmup / outcome | Selection start / length / replacement | Operation / input path | Synchronous / render / observation / envelope ms | Allocated bytes / GC 0,1,2 | Dispatcher samples / missed / max delay ms | Characters / selection / caret / version age | SHA-256 |");
        markdown.AppendLine("|---|---|---|---:|---|---|---|---|");
        foreach (var run in runs)
        {
            var editor = run.Editor!;
            foreach (var operation in editor.Operations!)
            {
                var probe = operation.Dispatcher;
                markdown.AppendLine(FormattableString.Invariant(
                    $"| {run.Settings.Profile} / {run.Settings.SizeBytes} / {run.Repetition} / {run.Warmup} / {run.Outcome} | {editor.InitialSelectionStart} / {editor.InitialSelectionLength} / {editor.Replacement} | {operation.Name} / {operation.InputMethod} | {operation.SynchronousMilliseconds:0.###} / {operation.LayoutRenderOpportunityMilliseconds:0.###} / {operation.ObservationMilliseconds:0.###} / {operation.DurationMilliseconds:0.###} | {operation.AllocatedBytes} / {string.Join(',', operation.CollectionsByGeneration)} | {probe.SampleCount} / {probe.MissedIntervals} / {probe.MaximumDelayMilliseconds:0.###} | {operation.DocumentCharacters} / {operation.SelectionStart}+{operation.SelectionLength} / {operation.CaretOffset} / {operation.VersionAge} | {operation.DocumentSha256} |"));
            }
        }
        markdown.AppendLine("\nRaw queue delays, layout-event counts and per-operation observations remain in JSON, including completed steps before a later failure/timeout. Scenario metrics sum the four operation envelopes; validation and report serialization between operations are excluded. Top-level Editor timings sum operations; its Dispatcher is the latest operation's probe, not a combined distribution. GC deltas do not establish pause attribution. Fixture and expected strings remain alive through diagnostic GC outside timing. Background highlighting completion, native input/display latency, stall-free behavior and reliable operation p95 are not claimed.\n");
        markdown.AppendLine("| Profile / bytes / operation | Fixture SHA-256 | Successful measured samples | Synchronous median [min, max] ms |\n|---|---|---:|---:|");
        foreach (var group in runs.Where(run => !run.Warmup && run.Outcome == "completed")
            .SelectMany(run => run.Editor!.Operations!.Select(operation => (run.Settings, FixtureHash: run.Fixture?.Sha256, Operation: operation)))
            .GroupBy(item => (item.Settings, item.FixtureHash, item.Operation.Name)))
        {
            var values = group.Select(item => item.Operation.SynchronousMilliseconds).Order().ToArray();
            var middle = values.Length / 2;
            var median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
            markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {group.Key.Settings.Profile} / {group.Key.Settings.SizeBytes} / {group.Key.Name} | {group.Key.FixtureHash} | {values.Length} | {median:0.###} [{values[0]:0.###}, {values[^1]:0.###}] |"));
        }
    }
}
