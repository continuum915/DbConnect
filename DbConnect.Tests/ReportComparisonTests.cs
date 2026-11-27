using System.Text.Json;
using System.Text.Json.Nodes;
using DbConnect.PerformanceTests;
using PerformanceProgram = DbConnect.PerformanceTests.Program;

namespace DbConnect.Tests;

public sealed class ReportComparisonTests
{
    [Fact]
    public void MatchingReportsShowSignedMedianDifferencesWithUnits()
    {
        var comparison = ReportComparer.Compare(Fixture(10, 20, 30), Fixture(20, 30, 40));
        var group = Assert.Single(comparison.Groups);
        Assert.Equal("compatible", group.Compatibility);
        var metric = Assert.Single(group.Metrics, x => x.Name == "Metrics.DurationMilliseconds");
        Assert.Equal("ms", metric.Unit);
        Assert.Equal(20, metric.Baseline.Values!.Median);
        Assert.Equal(10, metric.Baseline.Values.Minimum);
        Assert.Equal(30, metric.Baseline.Values.Maximum);
        Assert.Equal(10, metric.AbsoluteDifference);
        Assert.Equal(50, metric.PercentageDifference);
    }

    [Theory]
    [InlineData("ScenarioId")]
    [InlineData("Profile")]
    [InlineData("SizeBytes")]
    [InlineData("GeneratorVersion")]
    [InlineData("Seed")]
    [InlineData("Sha256")]
    [InlineData("SamplingMilliseconds")]
    [InlineData("TimeoutSeconds")]
    [InlineData("Policy")]
    public void ChangedWorkloadOrMeasurementSettingsNeverProduceDeltas(string field)
    {
        var baseline = WorkloadFixture();
        var candidate = baseline.DeepClone();
        if (field == "Policy") candidate[field] = "Different observation window.";
        else if (field is "SamplingMilliseconds" or "TimeoutSeconds") candidate["Settings"]![field] = 99;
        else if (field is "ScenarioId" or "Profile") candidate["Runs"]![0]!["Settings"]![field] = "different";
        else if (field == "SizeBytes") candidate["Runs"]![0]!["Settings"]![field] = 8192;
        else if (field == "Seed") candidate["Runs"]![0]!["Fixture"]![field] = 99;
        else candidate["Runs"]![0]!["Fixture"]![field] = "different";
        var result = ReportComparer.Compare(Element(baseline), Element(candidate));
        Assert.DoesNotContain(result.Groups, group => group.Compatibility == "compatible");
        Assert.All(result.Groups, group => Assert.Empty(group.Metrics));
        Assert.Contains(result.Groups.SelectMany(x => x.Reasons), reason => reason.Contains(field));
    }

    [Theory]
    [InlineData("InputMethod")]
    [InlineData("WindowWidth")]
    [InlineData("InputOffset")]
    [InlineData("LineTransformers")]
    [InlineData("CadenceMilliseconds")]
    public void EditorSettingsMustMatch(string field)
    {
        var baseline = EditorFixture(20);
        var candidate = baseline.DeepClone();
        var editor = candidate["Runs"]![0]!["Editor"]!;
        if (field == "CadenceMilliseconds") editor["Dispatcher"]![field] = 50;
        else if (field == "LineTransformers") editor[field] = new JsonArray("BareEditor");
        else if (field == "InputMethod") editor[field] = "document-insert";
        else editor[field] = 99;
        var result = ReportComparer.Compare(Element(baseline), Element(candidate));
        Assert.DoesNotContain(result.Groups, group => group.Compatibility == "compatible");
        Assert.Contains(result.Groups.SelectMany(x => x.Reasons), reason => reason.Contains(field));
    }

    [Fact]
    public void WarmupsAndFailedRunsStayRawButNeverEnterTimingStatistics()
    {
        var baseline = JsonNode.Parse(Fixture(10, 999, 500).GetRawText())!;
        baseline["Runs"]![1]!["Outcome"] = "timeout";
        baseline["Runs"]![1]!["Failure"] = "deadline";
        baseline["Runs"]![2]!["Warmup"] = true;
        var candidate = baseline.DeepClone();
        candidate["Runs"]![0]!["Metrics"]!["DurationMilliseconds"] = 20;
        var result = ReportComparer.Compare(Element(baseline), Element(candidate));
        var metric = Assert.Single(result.Groups).Metrics.Single(x => x.Name == "Metrics.DurationMilliseconds");
        Assert.Equal(10, metric.Baseline.Values!.Median);
        Assert.Equal(1, metric.Baseline.Samples);
        Assert.Equal(3, result.Baseline.GetProperty("Runs").GetArrayLength());
        Assert.Equal("deadline", result.Baseline.GetProperty("Runs")[1].GetProperty("Failure").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroOrMissingBaselinesNeverManufacturePercentages(bool missing)
    {
        var baseline = JsonNode.Parse(Fixture(0).GetRawText())!;
        if (missing) baseline["Runs"]![0]!["Metrics"] = null;
        var result = ReportComparer.Compare(Element(baseline), Fixture(10));
        var metric = Assert.Single(result.Groups).Metrics.Single(x => x.Name == "Metrics.DurationMilliseconds");
        Assert.Null(metric.PercentageDifference);
        Assert.Equal(missing ? null : 10d, metric.AbsoluteDifference);
        Assert.Contains(missing ? "missing" : "zero", metric.Note);
    }
    [Theory]
    [InlineData("Runtime")]
    [InlineData("Machine")]
    [InlineData("ProcessorCount")]
    [InlineData("CpuModel")]
    public void EnvironmentChangesRemainVisibleWithoutChangingWorkloadIdentity(string field)
    {
        var candidate = JsonNode.Parse(Fixture(20).GetRawText())!;
        if (field == "ProcessorCount") candidate["Metadata"]![field] = 16;
        else candidate["Metadata"]![field] = "changed";
        var result = ReportComparer.Compare(Fixture(10), Element(candidate));
        Assert.Equal("compatible", Assert.Single(result.Groups).Compatibility);
        Assert.Contains(result.EnvironmentWarnings, warning => warning.Contains(field));
        if (field == "ProcessorCount") Assert.Equal(16, result.Candidate.GetProperty("Metadata").GetProperty(field).GetInt32());
        else Assert.Equal("changed", result.Candidate.GetProperty("Metadata").GetProperty(field).GetString());
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void DispatcherPercentilesRequireEnoughRawSamplesAndDiscloseCounts(int count, bool available)
    {
        var report = EditorFixture(count);
        var result = ReportComparer.Compare(Element(report), Element(report));
        var metric = Assert.Single(result.Groups).Metrics.Single(x => x.Name == "Editor.Dispatcher.P95Milliseconds");
        Assert.Equal(available, metric.Baseline.Values != null);
        Assert.Equal(new[] { count }, metric.Baseline.DispatcherSampleCounts);
        if (available) Assert.Equal(2, metric.Baseline.Values!.Median); // Derived from raw delays, not the supplied p95 of 3.
        Assert.DoesNotContain(result.Groups.SelectMany(x => x.Metrics), x => x.Name == "Metrics.DurationP95Milliseconds");
    }
    [Fact]
    public async Task CompareCommandWritesReadableAndRawEvidenceWithoutChangingInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rain-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var baseline = Path.Combine(directory, "baseline.json");
            var candidate = Path.Combine(directory, "candidate.json");
            var output = Path.Combine(directory, "comparison");
            var before = Fixture(10).GetRawText();
            await File.WriteAllTextAsync(baseline, before);
            var changed = JsonNode.Parse(Fixture(20).GetRawText())!;
            changed["Runs"]![0]!["Outcome"] = "timeout";
            changed["Runs"]![0]!["Failure"] = "deadline";
            await File.WriteAllTextAsync(candidate, changed.ToJsonString());
            Assert.Equal(0, await PerformanceProgram.Main(["--compare", baseline, candidate, "--output", output]));
            var markdown = await File.ReadAllTextAsync(Path.Combine(output, "comparison.md"));
            Assert.Contains("median [min, max]", markdown);
            Assert.Contains("timeout", markdown);
            Assert.Contains("deadline", markdown);
            Assert.Contains("No operation p95", markdown);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "comparison.json")));
            Assert.Single(json.RootElement.GetProperty("Candidate").GetProperty("Runs").EnumerateArray());
            Assert.Equal(before, await File.ReadAllTextAsync(baseline));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void MissingFixtureIdentityCannotEstablishCompatibilityEvenOnBothSides()
    {
        var report = WorkloadFixture();
        report["Runs"]![0]!["Fixture"] = new JsonObject();
        var result = ReportComparer.Compare(Element(report), Element(report));
        Assert.Equal("incompatible", Assert.Single(result.Groups).Compatibility);
        Assert.Empty(result.Groups[0].Metrics);
        Assert.Contains(result.Groups[0].Reasons, reason => reason.Contains("GeneratorVersion"));
    }

    [Fact]
    public void GenerationTimeAndObjectPropertyOrderDoNotChangeFixtureIdentity()
    {
        var baseline = WorkloadFixture();
        var candidate = baseline.DeepClone();
        candidate["Runs"]![0]!["Fixture"]!["GenerationMilliseconds"] = 99999;
        var fields = (JsonObject)candidate["Runs"]![0]!["Settings"]!;
        candidate["Runs"]![0]!["Settings"] = new JsonObject(fields.Reverse().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value?.DeepClone())));
        Assert.Equal("compatible", Assert.Single(ReportComparer.Compare(Element(baseline), Element(candidate)).Groups).Compatibility);
    }

    [Fact]
    public void AllTimeoutsHaveUnavailableMetricsAndKeepEveryRawMemorySample()
    {
        var report = JsonNode.Parse(Fixture(99).GetRawText())!;
        report["Runs"]![0]!["Outcome"] = "timeout";
        var result = ReportComparer.Compare(Element(report), Element(report));
        var metric = Assert.Single(result.Groups).Metrics.Single(x => x.Name == "Metrics.DurationMilliseconds");
        Assert.Null(metric.Baseline.Values);
        Assert.Null(metric.AbsoluteDifference);
        Assert.Single(result.Baseline.GetProperty("Runs")[0].GetProperty("MemorySamples").EnumerateArray());
    }

    [Fact]
    public void MismatchedDispatcherCountDoesNotEnablePercentiles()
    {
        var report = EditorFixture(20);
        report["Runs"]![0]!["Editor"]!["Dispatcher"]!["QueueDelaysMilliseconds"] = new JsonArray(1d);
        var result = ReportComparer.Compare(Element(report), Element(report));
        Assert.Null(result.Groups[0].Metrics.Single(x => x.Name == "Editor.Dispatcher.P95Milliseconds").Baseline.Values);
    }
    [Fact]
    public void DifferentSampleCountsUseEvenMediansAndSignedByteDifferences()
    {
        var baseline = JsonNode.Parse(Fixture(10, 20).GetRawText())!;
        var candidate = JsonNode.Parse(Fixture(5).GetRawText())!;
        candidate["Runs"]![0]!["Metrics"]!["AllocatedBytes"] = 50;
        var group = Assert.Single(ReportComparer.Compare(Element(baseline), Element(candidate)).Groups);
        var duration = group.Metrics.Single(x => x.Name == "Metrics.DurationMilliseconds");
        Assert.Equal(15, duration.Baseline.Values!.Median);
        Assert.Equal(-10, duration.AbsoluteDifference);
        var allocation = group.Metrics.Single(x => x.Name == "Metrics.AllocatedBytes");
        Assert.Equal("bytes", allocation.Unit);
        Assert.Equal(-50, allocation.AbsoluteDifference);
        Assert.Equal(-50, allocation.PercentageDifference);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("json")]
    [InlineData("overwrite")]
    public async Task InvalidInputsNeverOverwriteReportsOrProduceComparison(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rain-invalid-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "comparison.json");
            var report = JsonNode.Parse(Fixture(10).GetRawText())!;
            if (failure == "schema") report["SchemaVersion"] = 999;
            var before = failure == "json" ? "invalid json" : report.ToJsonString();
            await File.WriteAllTextAsync(source, before);
            var output = failure == "overwrite" ? directory : Path.Combine(directory, "output");
            Assert.Equal(2, await PerformanceProgram.Main(["--compare", source, source, "--output", output]));
            Assert.Equal(before, await File.ReadAllTextAsync(source));
            Assert.False(File.Exists(Path.Combine(output, "comparison.md")));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void DifferentObservationTargetsCannotBeComparedAsTimingChanges()
    {
        var baseline = EditorFixture(20);
        baseline["EditorProtocols"] = JsonSerializer.SerializeToNode(new Dictionary<string, object>
        {
            ["editor.paste"] = new { Version = 1, DeferredObservationTargetMilliseconds = 300 }
        });
        var candidate = baseline.DeepClone();
        candidate["EditorProtocols"]!["editor.paste"]!["DeferredObservationTargetMilliseconds"] = 500;
        var result = ReportComparer.Compare(Element(baseline), Element(candidate));
        Assert.DoesNotContain(result.Groups, group => group.Compatibility == "compatible");
        Assert.Contains(result.Groups.SelectMany(x => x.Reasons), reason => reason.Contains("DeferredObservationTargetMilliseconds"));
    }
    [Fact]
    public void LegacyEditorReportsWithoutIntendedWindowsAreExplicitlyIncompatible()
    {
        var report = EditorFixture(20);
        ((JsonObject)report).Remove("EditorProtocols");
        var group = Assert.Single(ReportComparer.Compare(Element(report), Element(report)).Groups);
        Assert.Equal("incompatible", group.Compatibility);
        Assert.Empty(group.Metrics);
        Assert.Contains(group.Reasons, reason => reason.Contains("legacy observation settings"));
    }

    [Fact]
    public async Task MeasurementWriterPersistsTheObservationProtocolAlongsideRawRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rain-protocol-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runs = Element(EditorFixture(20)).GetProperty("Runs").Deserialize<RunRecord[]>()!;
            await ReportWriter.WriteAsync(directory, runs, new(directory, ["editor.paste"], 1, 15, 20));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            var protocol = report.RootElement.GetProperty("EditorProtocols").GetProperty("editor.paste");
            Assert.Equal(1, protocol.GetProperty("Version").GetInt32());
            Assert.Equal(300, protocol.GetProperty("DeferredObservationTargetMilliseconds").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static JsonNode EditorFixture(int count)
    {
        var report = WorkloadFixture();
        report["Runs"]![0]!["Settings"]!["ScenarioId"] = "editor.paste";
        report["EditorProtocols"] = JsonSerializer.SerializeToNode(EditorProtocolSettings.Capture(["editor.paste"]));
        var dispatcher = new DispatcherObservations(20, count, 0, 3, 2, 3, Enumerable.Repeat(2d, count).ToArray());
        report["Runs"]![0]!["Editor"] = JsonSerializer.SerializeToNode(new EditorObservations("clipboard-paste", 4096,
            "fixture-hash", "isolated", 1, 2, 3, 4, 5, 300, 1, true, false, ["TextMate"], dispatcher));
        return report;
    }
    private static JsonNode WorkloadFixture()
    {
        var report = JsonNode.Parse(Fixture(10).GetRawText())!;
        report["Runs"]![0]!["Settings"] = JsonSerializer.SerializeToNode(new RunSettings("prepare.split", "representative", 4096));
        report["Runs"]![0]!["Fixture"] = JsonSerializer.SerializeToNode(RepresentativeImport.Generate(4096).Metadata);
        return report;
    }

    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    private static JsonElement Fixture(params double[] durations)
    {
        var runs = durations.Select((duration, index) => new RunRecord(new("harness.noop"), index + 1, false,
            "completed", 0, "complete", [new(1, 200, 100)], new(duration, 100, [0, 0, 0], 200, 150), null, 50)).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = 1,
            Metadata = new EnvironmentMetadata("reference", "Windows", ".NET 10", "X64", 8, "Release", "abc", "clean", new()),
            Settings = new RunnerOptions("ignored-output", ["harness.noop"], durations.Length, 15, 20),
            Policy = "One discarded warm-up; fresh sequential children.",
            Runs = runs
        });
    }
}
