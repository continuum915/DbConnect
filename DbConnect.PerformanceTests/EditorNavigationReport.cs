using System.Globalization;
using System.Text;

namespace DbConnect.PerformanceTests;

public static class EditorNavigationReport
{
    public static void Append(StringBuilder markdown, IEnumerable<RunRecord> records)
    {
        var runs = records.Where(run => run.Editor?.Navigation != null).ToArray();
        if (runs.Length == 0) return;
        markdown.AppendLine("\n## Navigation and viewport scrolling\n");
        markdown.AppendLine("Real 1000x700 QueryTabView with production highlighting. Each child loads the deterministic fixture at the top, pauses 300 ms, moves to the line containing the middle character, scrolls three single viewports down/up, moves to EOF, then scrolls one viewport up/down. Navigation uses CaretOffset + ScrollToLine; scrolling uses the owning ScrollViewer.Offset. These are programmatic paths, not mouse-wheel or key simulation. Scroll distance is the actual pre-operation text-viewport height in device-independent pixels (DIP), clamped to its observed extent. No-op/clamped movement remains visible in raw offsets.\n");
        foreach (var run in runs)
        {
            var editor = run.Editor!;
            var settings = editor.Navigation!;
            markdown.AppendLine(FormattableString.Invariant($"- {run.Settings.Profile}, {run.Settings.SizeBytes} bytes, repetition {run.Repetition}, warmup={run.Warmup}, outcome={run.Outcome}: middle offset/line={settings.MiddleOffset}/{settings.MiddleLine}; EOF offset/line={settings.EndOffset}/{settings.EndLine}; viewports per scroll={settings.ViewportsPerScroll}; steps per direction={settings.ScrollStepsPerDirection}; word wrap={settings.WordWrap}; initial text viewport={settings.InitialViewport?.Width:0.###}x{settings.InitialViewport?.Height:0.###} DIP; cold startup/warm-up ms={editor.ColdStartupMilliseconds:0.###}/{editor.WarmupMilliseconds:0.###}."));
        }
        markdown.AppendLine("\n| Profile / bytes / repetition / warmup / outcome | Operation / path | Target line / requested Y / clamped Y | Viewport width x height DIP | Y before / after | Visible lines after | Sync / render / observation / envelope ms | Allocated bytes / GC 0,1,2 | Dispatcher samples / missed / max delay ms |");
        markdown.AppendLine("|---|---|---|---:|---:|---|---:|---|---|");
        foreach (var run in runs)
            foreach (var operation in run.Editor!.Operations ?? [])
            {
                var viewport = operation.Viewport!;
                var probe = operation.Dispatcher;
                markdown.AppendLine(FormattableString.Invariant(
                    $"| {run.Settings.Profile} / {run.Settings.SizeBytes} / {run.Repetition} / {run.Warmup} / {run.Outcome} | {operation.Name} / {operation.InputMethod} | {viewport.TargetLine} / {F(viewport.RequestedY)} / {F(viewport.ClampedTargetY)} | {viewport.Before.Width:0.###} x {viewport.Before.Height:0.###} | {viewport.Before.Y:0.###} / {viewport.After.Y:0.###} | {viewport.After.FirstVisibleLine}–{viewport.After.LastVisibleLine} | {operation.SynchronousMilliseconds:0.###} / {operation.LayoutRenderOpportunityMilliseconds:0.###} / {operation.ObservationMilliseconds:0.###} / {operation.DurationMilliseconds:0.###} | {operation.AllocatedBytes} / {string.Join(',', operation.CollectionsByGeneration)} | {probe.SampleCount} / {probe.MissedIntervals} / {probe.MaximumDelayMilliseconds:0.###} |"));
            }
        markdown.AppendLine("\nOperation envelopes include a background dispatcher drain, layout/frame opportunity, bounded 300 ms observation and final synchronized probe. Exact document/model content is compared by streaming snapshots outside timing; caret, selection, version and viewport checks also run outside counters. Post-operation heap is captured before these checks. No forced GC occurs during operations; fixture/oracle lifetime continues through separate diagnostic GC. Scenario totals sum operation envelopes, excluding setup/validation/protocol gaps. Top-level Editor timings sum steps and its Dispatcher is the latest step, not a pooled distribution. Raw queue samples, offsets, viewport/extent sizes, visible lines, hashes and partial successful steps survive in JSON even if a later step fails/times out. Frames describe headless layout/render opportunities; native FPS, physical input-to-display latency, completed background highlighting and reliable operation p95 are not claimed.\n");
        markdown.AppendLine("| Profile / bytes / operation | Fixture SHA-256 | Window / pre-operation viewport DIP / wrap | Successful measured samples | Synchronous median [min, max] ms | Render-opportunity median [min, max] ms |\n|---|---|---|---:|---:|---:|");
        foreach (var group in runs.Where(run => !run.Warmup && run.Outcome == "completed")
            .SelectMany(run => (run.Editor!.Operations ?? []).Select(operation => (Run: run, Operation: operation)))
            .GroupBy(item => (item.Run.Settings, Fixture: item.Run.Fixture?.Sha256, item.Run.Editor!.Navigation,
                item.Run.Editor.WindowWidth, item.Run.Editor.WindowHeight,
                item.Operation.Viewport!.Before.Width, item.Operation.Viewport.Before.Height, item.Operation.Name)))
        {
            var synchronous = ReportWriter.DistributionOf(group.Select(item => item.Operation.SynchronousMilliseconds));
            var render = ReportWriter.DistributionOf(group.Select(item => item.Operation.LayoutRenderOpportunityMilliseconds));
            markdown.AppendLine(FormattableString.Invariant($"| {group.Key.Settings.Profile} / {group.Key.Settings.SizeBytes} / {group.Key.Name} | {group.Key.Fixture} | {group.Key.WindowWidth}x{group.Key.WindowHeight} / {group.Key.Width:0.###}x{group.Key.Height:0.###} / {group.Key.Navigation!.WordWrap} | {group.Count()} | {ReportWriter.Format(synchronous)} | {ReportWriter.Format(render)} |"));
        }
    }

    private static string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
}
