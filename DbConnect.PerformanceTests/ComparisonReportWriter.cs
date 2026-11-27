using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public static class ComparisonReportWriter
{
    public static async Task WriteAsync(string output, ReportComparison comparison)
    {
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new
        {
            ComparisonSchemaVersion = 1,
            comparison.Baseline,
            comparison.Candidate,
            comparison.EnvironmentWarnings,
            comparison.Groups
        }, new JsonSerializerOptions { WriteIndented = true }));
        var text = new StringBuilder("# Measurement report comparison\n\n");
        text.AppendLine("Informational differences: candidate minus baseline. No performance pass/fail budgets. Only compatible workload/settings groups receive deltas. Environment changes are flagged separately and prevent a controlled-comparison interpretation.\n");
        text.AppendLine("Statistics exclude warm-ups and unsuccessful runs. Each value is a median [min, max] across available successful measured runs, with counts and missing values disclosed. No operation p95 is claimed from five samples. Dispatcher percentiles require at least 20 valid raw delays per run and matching declared counts; table distributions summarize those per-run percentiles, not pooled queue samples.\n");
        text.AppendLine("Both complete input reports, including raw memory/dispatcher samples, warm-ups, failures, timeouts, phases and all environment/package metadata, are embedded in [comparison.json](comparison.json). Source reports are unchanged. Machine/architecture/processor count are the recorded hardware indicators; schema 1 cannot establish identical CPU model, RAM or system load when these are absent.\n");
        text.AppendLine("| Environment | Baseline | Candidate |\n|---|---|---|");
        foreach (var field in new[] { "Machine", "Os", "Runtime", "Architecture", "ProcessorCount", "BuildConfiguration", "Commit", "Dirty" })
            text.AppendLine($"| {field} | {Cell(ComparisonIdentity.At(comparison.Baseline, "Metadata." + field))} | {Cell(ComparisonIdentity.At(comparison.Candidate, "Metadata." + field))} |");
        foreach (var warning in comparison.EnvironmentWarnings) text.AppendLine($"\nEnvironment warning: {Escape(warning)}\n");
        text.AppendLine("\n## Workload compatibility\n");
        text.AppendLine("Run indices below are zero-based positions in the embedded Baseline.Runs / Candidate.Runs arrays. Each group uses exact recorded settings/fixture identity, ignoring only fixture generation time; different sample counts and output paths do not change a workload.\n");
        text.AppendLine("| Group / scenario | Status | Baseline indices | Candidate indices | Reasons |\n|---|---|---|---|---|");
        foreach (var (group, index) in comparison.Groups.Select((x, i) => (x, i)))
            text.AppendLine($"| {index} / {Escape(group.ScenarioId)} | {group.Compatibility} | {string.Join(", ", group.BaselineRunIndices)} | {string.Join(", ", group.CandidateRunIndices)} | {Escape(string.Join(" ", group.Reasons))} |");
        text.AppendLine("\n## Metric differences\n");
        text.AppendLine("Units apply to both distributions and the absolute delta. Unavailable percentages include zero or missing baselines. Missing counts refer to successful measured runs without a valid value, not failed runs; all outcomes are listed below.\n");
        text.AppendLine("| Group / metric | Unit | Baseline median [min, max] | Candidate median [min, max] | Absolute delta | Percent delta | Valid / missing runs (baseline; candidate) | Dispatcher counts per run (baseline; candidate) | Note |\n|---|---|---:|---:|---:|---:|---|---|---|");
        foreach (var (group, index) in comparison.Groups.Select((x, i) => (x, i)))
            foreach (var metric in group.Metrics)
                text.AppendLine($"| {index} / {Escape(metric.Name)} | {metric.Unit} | {ReportWriter.Format(metric.Baseline.Values)} | {ReportWriter.Format(metric.Candidate.Values)} | {Number(metric.AbsoluteDifference)} | {Number(metric.PercentageDifference)}{(metric.PercentageDifference.HasValue ? "%" : "")} | {metric.Baseline.Samples} / {metric.Baseline.MissingSamples}; {metric.Candidate.Samples} / {metric.Candidate.MissingSamples} | {string.Join(",", metric.Baseline.DispatcherSampleCounts)}; {string.Join(",", metric.Candidate.DispatcherSampleCounts)} | {Escape(metric.Note)} |");
        AppendRuns(text, "Baseline", comparison.Baseline);
        AppendRuns(text, "Candidate", comparison.Candidate);
        await File.WriteAllTextAsync(Path.Combine(output, "comparison.md"), text.ToString());
    }

    private static void AppendRuns(StringBuilder text, string side, JsonElement report)
    {
        var runs = report.GetProperty("Runs").EnumerateArray().ToArray();
        text.AppendLine($"\n## {side} outcomes\n");
        foreach (var group in runs.GroupBy(x => x.GetProperty("Outcome").GetString()))
            text.AppendLine($"- {Escape(group.Key ?? "unknown")}: {group.Count()} total, {group.Count(x => x.GetProperty("Warmup").GetBoolean())} warm-ups.");
        text.AppendLine("\n| Index | Scenario / profile / bytes | Repetition / warm-up | Outcome / exit | Last phase | Memory samples | Failure |\n|---|---|---|---|---|---:|---|");
        foreach (var (run, index) in runs.Select((x, i) => (x, i)))
        {
            var samples = ComparisonIdentity.At(run, "MemorySamples");
            text.AppendLine($"| {index} | {Cell(ComparisonIdentity.At(run, "Settings.ScenarioId"))} / {Cell(ComparisonIdentity.At(run, "Settings.Profile"))} / {Cell(ComparisonIdentity.At(run, "Settings.SizeBytes"))} | {Cell(ComparisonIdentity.At(run, "Repetition"))} / {Cell(ComparisonIdentity.At(run, "Warmup"))} | {Cell(ComparisonIdentity.At(run, "Outcome"))} / {Cell(ComparisonIdentity.At(run, "ExitCode"))} | {Cell(ComparisonIdentity.At(run, "LastCompletedPhase"))} | {(samples.ValueKind == JsonValueKind.Array ? samples.GetArrayLength() : 0)} | {Cell(ComparisonIdentity.At(run, "Failure"))} |");
        }
    }

    private static string Number(double? value) => value?.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Cell(JsonElement value) => Escape(value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "unavailable",
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText()
    });
    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
