using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public sealed record Distribution(double Median, double Minimum, double Maximum);
public sealed record ScenarioSummary(RunSettings Settings, int SuccessfulSamples, int FailedSamples,
    Distribution? DurationMilliseconds, Distribution? AllocatedBytes, Distribution? PostOperationHeapBytes,
    Distribution? ForcedGcDiagnosticHeapBytes, Distribution? PeakPrivateBytes, Distribution? PeakWorkingSetBytes);

public static class ReportWriter
{
    public static List<ScenarioSummary> Summarize(IEnumerable<RunRecord> records) => records
        .Where(run => !run.Warmup).GroupBy(run => run.Settings).Select(group =>
        {
            var successful = group.Where(run => run.Outcome == "completed" && run.Metrics is not null).ToArray();
            return new ScenarioSummary(group.Key, successful.Length, group.Count() - successful.Length,
                DistributionOf(successful.Select(run => run.Metrics!.DurationMilliseconds)),
                DistributionOf(successful.Select(run => (double)run.Metrics!.AllocatedBytes)),
                DistributionOf(successful.Select(run => (double)run.Metrics!.PostOperationHeapBytes)),
                DistributionOf(successful.Select(run => (double)run.Metrics!.ForcedGcDiagnosticHeapBytes)),
                DistributionOf(successful.Where(run => run.MemorySamples.Count > 0)
                    .Select(run => (double)run.MemorySamples.Max(sample => sample.PrivateBytes))),
                DistributionOf(successful.Where(run => run.MemorySamples.Count > 0)
                    .Select(run => (double)run.MemorySamples.Max(sample => sample.WorkingSetBytes))));
        }).ToList();

    public static async Task WriteAsync(string output, IReadOnlyList<RunRecord> records, RunnerOptions options)
    {
        Directory.CreateDirectory(output);
        var summaries = Summarize(records);
        var metadata = await EnvironmentMetadata.CaptureAsync();
        var coverage = ModeCoverage.Capture(options, records);
        var report = new
        {
            SchemaVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            Metadata = metadata,
            Settings = options,
            Coverage = coverage,
            UnsupportedMeasurements = ModeCoverage.Limitations,
            Policy = "Sequential fresh child per scenario/profile/size/repetition; one discarded warm-up per scenario. " +
                "Harness fixtures: none (profile=harness, size=0). Representative SQL and generator oracle remain alive through diagnostic GC. Idle waits 100 ms; noop does no work. " +
                "Operation time uses Stopwatch; allocations are process-wide and include async instrumentation noise. " +
                "Startup, fixture preparation, validation, protocol serialization and diagnostic GC are outside operation counters. " +
                "Heap is GC.GetTotalMemory(false) immediately after operation; diagnostic heap uses GC.GetTotalMemory(true) afterward. " +
                "Idle memory is sampled at the ready handshake. Peaks cover the entire supervised child lifetime, including startup and GC. " +
                "Parent phase timestamps measure message receipt since launch, not child phase execution duration. " +
                "Sampling cadence is requested, scheduler-dependent, and may miss brief spikes. No operation p95 is claimed.",
            EditorProtocols = EditorProtocolSettings.Capture(records.Select(run => run.Settings.ScenarioId)),
            Runs = records,
            Summaries = summaries
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"),
            JsonSerializer.Serialize(new
            {
                options.Mode,
                Metadata = metadata,
                Coverage = coverage,
                UnsupportedMeasurements = ModeCoverage.Limitations
            }, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = new StringBuilder("# Supervised performance measurements\n\n");
        markdown.AppendLine($"Mode: {options.Mode ?? "custom"}. Performance results are informational only.\n");
        foreach (var item in coverage)
            markdown.AppendLine($"- {item.Settings}: {item.Outcome}; children {item.RecordedRuns}/{item.ExpectedRuns}. {item.Reason}");
        foreach (var item in ModeCoverage.Limitations)
            markdown.AppendLine($"- {item.Measurement}: {item.Outcome}. {item.Reason}");
        markdown.AppendLine($"Machine: {metadata.Machine}; {metadata.Os}; {metadata.Runtime}; {metadata.BuildConfiguration}.");
        markdown.AppendLine($"Commit: {metadata.Commit}; dirty: {metadata.Dirty}. Requested sampling: {options.SamplingMilliseconds} ms.");
        markdown.AppendLine($"\n{report.Policy}\n");
        markdown.AppendLine("Medians and ranges exclude warm-ups and failed runs; all runs remain in JSON and the table below.\n");
        markdown.AppendLine("| Scenario | Success / failure | Operation ms: median [min, max] | Allocated bytes: median [min, max] |");
        markdown.AppendLine("|---|---:|---:|---:|");
        foreach (var summary in summaries)
            markdown.AppendLine($"| {summary.Settings.ScenarioId} ({summary.Settings.Profile}, {summary.Settings.SizeBytes} bytes) | {summary.SuccessfulSamples} / {summary.FailedSamples} | {Format(summary.DurationMilliseconds)} | {Format(summary.AllocatedBytes)} |");
        markdown.AppendLine("\n| Scenario | Repetition | Outcome | Exit | Last phase | Idle private / working set bytes | Peak private / working set bytes |");
        markdown.AppendLine("|---|---:|---|---:|---|---:|---:|");
        foreach (var run in records)
        {
            var peakPrivate = run.MemorySamples.Select(sample => sample.PrivateBytes).DefaultIfEmpty().Max();
            var peakWorking = run.MemorySamples.Select(sample => sample.WorkingSetBytes).DefaultIfEmpty().Max();
            markdown.AppendLine($"| {run.Settings.ScenarioId} ({run.Settings.Profile}, {run.Settings.SizeBytes} bytes) | {run.Repetition}{(run.Warmup ? " (warm-up)" : "")} | {run.Outcome} | {run.ExitCode} | {run.LastCompletedPhase} | {run.IdleMemory?.PrivateBytes} / {run.IdleMemory?.WorkingSetBytes} | {peakPrivate} / {peakWorking} |");
        }
        markdown.AppendLine("\n## Generated fixtures\n");
        markdown.AppendLine("ASCII SQL, UTF-8 without BOM, CRLF; MiB = 1,048,576 bytes. Line counts include the final empty line. Generation time excludes scenario/tab setup and is outside operation timing.\n");
        markdown.AppendLine("| Scenario / bytes / repetition | Generator / seed / profile | SHA-256 | UTF-16 characters / lines / max line | GO / INSERTs / tables | Pairs per batch / literal padding / actual parameters | Generation ms |");
        markdown.AppendLine("|---|---|---|---:|---:|---|---:|");
        foreach (var run in records.Where(run => run.Fixture is not null))
        {
            var fixture = run.Fixture!;
            markdown.AppendLine($"| {run.Settings.ScenarioId} / {fixture.EncodedBytes} / {run.Repetition} | {fixture.GeneratorVersion} / {fixture.Seed} / {fixture.Profile} | {fixture.Sha256} | {fixture.Utf16Characters} / {fixture.Lines} / {fixture.MaximumLineLength} | {fixture.GoDirectives} / {fixture.Inserts} / {fixture.Tables} | {fixture.Configuration?.PairsPerBatch} / {fixture.Configuration?.LiteralPadding} / {fixture.Configuration?.ActualParameters} | {fixture.GenerationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} |");
        }
        markdown.AppendLine("\n## SQL Server execution-unit observations\n");
        foreach (var run in records.Where(run => run.Observations is not null))
        {
            var observed = run.Observations!;
            markdown.AppendLine($"- {run.Settings.ScenarioId}, {run.Settings.Profile}, {run.Settings.SizeBytes} bytes, repetition {run.Repetition}: INSERTs={observed.ExpectedInserts}; GO batches={observed.GoBatches}; split={observed.ParsedStatements}; spans={observed.SpanCount}; parameters={observed.DetectedParameters}.");
            foreach (var discrepancy in observed.ExecutionUnitDiscrepancies)
                markdown.AppendLine($"  - {discrepancy}");
        }
        markdown.AppendLine("\n## Direct batch execution\n");
        markdown.AppendLine("| Bytes / repetition / warmup | First submission ms | Commands | Submitted characters | Hash | Verified INSERTs | Results | Result retention bytes (with / without heap) |");
        markdown.AppendLine("|---|---:|---:|---:|---|---:|---:|---:|");
        foreach (var run in records.Where(run => run.Execution is not null))
        {
            var observed = run.Execution!;
            markdown.AppendLine(FormattableString.Invariant($"| {run.Settings.SizeBytes} / {run.Repetition} / {run.Warmup} | {observed.FirstSubmissionMilliseconds:0.###} | {observed.CommandCount} | {observed.SubmittedCharacters} | {observed.SubmissionHash:X16} | {observed.VerifiedInserts} | {observed.ResultCount} | {observed.RetainedResultBytes} ({observed.HeapWithResultsBytes} / {observed.HeapWithoutResultsBytes}) |"));

        }
        foreach (var discrepancy in records.Where(run => run.Execution is not null)
            .SelectMany(run => run.Execution!.ExecutionUnitDiscrepancies).Distinct())
            markdown.AppendLine($"\n{discrepancy}\n");
        markdown.AppendLine("\n## Isolated recovery persistence\n");
        foreach (var run in records.Where(run => run.Recovery is not null))
        {
            var recovery = run.Recovery!;
            markdown.AppendLine($"- {run.Settings.SizeBytes} bytes / repetition {run.Repetition}: {recovery.Attribution}; validated persisted bytes={recovery.PersistedBytes}; tabs={recovery.TabCount}. Storage: {recovery.Directory}; {recovery.DriveType}; {recovery.FileSystem}; available bytes before operation={recovery.AvailableFreeBytes}. {recovery.WriteSemantics}");
        }
        markdown.AppendLine("\n## Real query editor paste/insertion\n");
        markdown.AppendLine("Fixed 1000x700 real QueryTabView, production TextMate, Skia headless. Input timestamps and render opportunity are elapsed since command invocation. Operation envelope includes the bounded 300 ms deferred observation plus dispatcher drain. Startup, UI warm-up, clipboard preparation and content/hash validation are excluded. A rendered frame and valid visual lines do not establish background highlighting completion. Layout events are apparent activity; tokenizer queue state remains unknown. Clipboard is headless, not native Windows throughput. Allocations include UI/probe/frame/phase instrumentation.\n");
        markdown.AppendLine("| Scenario / bytes / repetition | Input method / offset | Cold startup / warm-up ms | Synchronous return / input complete / render ms | Observation ms / layout updates / visual lines valid | Dispatcher samples / missed slots / max delay ms | p50 / p95 ms | SHA-256 |");
        markdown.AppendLine("|---|---|---:|---:|---|---:|---:|---|");
        foreach (var run in records.Where(run => run.Editor != null && run.Editor.Operations == null && run.Editor.Integrated == null))
        {
            var editor = run.Editor!;
            var probe = editor.Dispatcher;
            string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "insufficient samples";
            markdown.AppendLine($"| {run.Settings.ScenarioId} / {run.Settings.SizeBytes} / {run.Repetition}{(run.Warmup ? " warm-up" : "")} | {editor.InputMethod} / {editor.InputOffset} | {F(editor.ColdStartupMilliseconds)} / {F(editor.WarmupMilliseconds)} | {F(editor.SynchronousInputMilliseconds)} / {F(editor.InputCompletedMilliseconds)} / {F(editor.LayoutRenderOpportunityMilliseconds)} | {F(editor.ObservationMilliseconds)} / {editor.LayoutUpdatesDuringObservation} / {editor.VisualLinesValidAtEnd} | {probe.SampleCount} / {probe.MissedIntervals} / {F(probe.MaximumDelayMilliseconds)} | {F(probe.P50Milliseconds)} / {F(probe.P95Milliseconds)} | {editor.DocumentSha256} |");
        }
        markdown.AppendLine("Dispatcher probes run at 20 ms cadence on a background producer, start before input, allow one pending UI callback, skip missed intervals without catch-up, and synchronize at both ends. Queue delays are retained in JSON; percentiles are omitted below 20 samples. Post-input observation is bounded rather than a declaration that TextMate has finished. Non-integrated editor scenarios: every child gets a fresh parent-assigned DBCONNECT_DATA_DIRECTORY; no MainWindow/recovery scheduler or live session is created. The parent removes the directory after child exit, including timeout/crash.\n");
        ExecutionStopReport.Append(markdown, records);
        IntegratedRunReport.Append(markdown, records);
        EditorSelectionReport.Append(markdown, records);
        EditorNavigationReport.Append(markdown, records);
        EditorReleaseReport.Append(markdown, records);
        foreach (var run in records.Where(run => run.Failure is not null))
            markdown.AppendLine($"\nFailure ({run.Settings.ScenarioId}, {run.Settings.Profile}, repetition {run.Repetition}): {run.Failure!.Replace("\n", " ").Replace("\r", " ")}\n");
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), markdown.ToString());
    }

    internal static Distribution? DistributionOf(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return null;
        var middle = sorted.Length / 2;
        return new(sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle],
            sorted[0], sorted[^1]);
    }

    internal static string Format(Distribution? value) => value is null ? "unavailable" :
        string.Create(CultureInfo.InvariantCulture, $"{value.Median:0.###} [{value.Minimum:0.###}, {value.Maximum:0.###}]");
}
