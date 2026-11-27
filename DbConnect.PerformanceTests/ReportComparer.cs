using System.Text.Json;

namespace DbConnect.PerformanceTests;

public sealed record ComparisonMetricSide(Distribution? Values, int Samples, int MissingSamples, int[] DispatcherSampleCounts);
public sealed record ComparisonMetric(string Name, string Unit, ComparisonMetricSide Baseline, ComparisonMetricSide Candidate,
    double? AbsoluteDifference, double? PercentageDifference, string Note);
public sealed record ComparisonGroup(string ScenarioId, string Compatibility, string[] Reasons,
    int[] BaselineRunIndices, int[] CandidateRunIndices, ComparisonMetric[] Metrics);
public sealed record ReportComparison(JsonElement Baseline, JsonElement Candidate, string[] EnvironmentWarnings, ComparisonGroup[] Groups);

public static class ReportComparer
{
    public static ReportComparison Compare(JsonElement baseline, JsonElement candidate)
    {
        var left = ComparisonIdentity.Read(baseline);
        var right = ComparisonIdentity.Read(candidate);
        var groups = new List<ComparisonGroup>();
        foreach (var key in left.Select(x => x.Key).Concat(right.Select(x => x.Key)).Distinct())
        {
            var a = left.Where(x => x.Key == key).ToArray();
            var b = right.Where(x => x.Key == key).ToArray();
            var exemplar = a.FirstOrDefault() ?? b[0];
            var reasons = a.Concat(b).SelectMany(x => x.Problems).Distinct().ToList();
            var compatible = a.Length > 0 && b.Length > 0 && reasons.Count == 0;
            var other = a.Length > 0 ? right : left;
            if (!compatible && (a.Length == 0 || b.Length == 0))
            {
                var related = other.Where(x => x.ScenarioId == exemplar.ScenarioId).ToArray();
                if (related.Length == 0) reasons.Add("Unmatched ScenarioId: no scenario on the other side.");
                else
                    foreach (var field in exemplar.Identity.Keys.Union(related.SelectMany(x => x.Identity.Keys)))
                        if (related.All(x => x.Identity.GetValueOrDefault(field) != exemplar.Identity.GetValueOrDefault(field)))
                            reasons.Add($"Incompatible {field}.");
                if (reasons.Count == 0) reasons.Add("No identical combination of fixture and scenario settings on the other side.");
            }
            var status = compatible ? "compatible" : other.All(x => x.ScenarioId != exemplar.ScenarioId)
                ? "unmatched" : "incompatible";
            groups.Add(new(exemplar.ScenarioId, status, reasons.ToArray(), a.Select(x => x.Index).ToArray(),
                b.Select(x => x.Index).ToArray(), compatible ? Metrics(a, b) : []));
        }
        return new(baseline.Clone(), candidate.Clone(), EnvironmentChanges(baseline, candidate), groups.ToArray());
    }

    private static string[] EnvironmentChanges(JsonElement baseline, JsonElement candidate)
    {
        var left = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var right = new SortedDictionary<string, string>(StringComparer.Ordinal);
        ComparisonIdentity.Add(left, "Metadata", baseline.GetProperty("Metadata"));
        ComparisonIdentity.Add(right, "Metadata", candidate.GetProperty("Metadata"));
        var ignored = new[] { "Metadata.Commit", "Metadata.Dirty", "Metadata.SourceIdentity" };
        var warnings = left.Keys.Union(right.Keys).Except(ignored)
            .Where(field => left.GetValueOrDefault(field) != right.GetValueOrDefault(field))
            .Select(field => $"Environment changed: {field}: {left.GetValueOrDefault(field) ?? "<missing>"} -> {right.GetValueOrDefault(field) ?? "<missing>"}; not a controlled comparison.")
            .ToList();
        foreach (var field in new[] { "Machine", "Os", "Runtime", "Architecture", "ProcessorCount", "BuildConfiguration" })
            if (ComparisonIdentity.At(baseline, "Metadata." + field).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                ComparisonIdentity.At(candidate, "Metadata." + field).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                warnings.Add($"Environment context unavailable: {field}; a controlled comparison cannot be established.");
        return warnings.ToArray();
    }
    private static ComparisonMetric[] Metrics(ComparisonRun[] left, ComparisonRun[] right)
    {
        var a = left.Where(x => ComparisonMeasurements.Eligible(x.Raw)).Select(x => ComparisonMeasurements.Read(x.Raw)).ToArray();
        var b = right.Where(x => ComparisonMeasurements.Eligible(x.Raw)).Select(x => ComparisonMeasurements.Read(x.Raw)).ToArray();
        var names = a.Concat(b).SelectMany(x => x.Keys).Append("Metrics.DurationMilliseconds").Distinct().Order(StringComparer.Ordinal);
        return names.Select(name =>
        {
            var baseline = Side(a, name);
            var candidate = Side(b, name);
            var difference = baseline.Values != null && candidate.Values != null
                ? candidate.Values.Median - baseline.Values.Median : (double?)null;
            var percent = baseline.Values is { Median: not 0 } ? difference / baseline.Values.Median * 100 : null;
            var unit = a.Concat(b).Select(x => x.GetValueOrDefault(name)?.Unit).FirstOrDefault(x => x != null) ?? "ms";
            var note = difference == null ? "missing baseline or candidate values; differences unavailable."
                : baseline.Values!.Median == 0 ? "zero baseline; percentage unavailable." : "Candidate minus baseline.";
            return new ComparisonMetric(name, unit, baseline, candidate, difference, percent, note);
        }).ToArray();
    }

    private static ComparisonMetricSide Side(Dictionary<string, ComparisonValue>[] runs, string name)
    {
        var observations = runs.Select(x => x.GetValueOrDefault(name)).ToArray();
        var values = observations.Select(x => x?.Value).OfType<double>().ToArray();
        return new(ReportWriter.DistributionOf(values), values.Length, runs.Length - values.Length,
            observations.Select(x => x?.DispatcherSamples).OfType<int>().ToArray());
    }
}