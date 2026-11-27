using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class PerformanceModeTests
{
    [Fact]
    public void SmokeKeepsCorrectnessWorkSmall()
    {
        var options = RunnerOptions.Parse(["--mode", "smoke"]);
        var runs = options.Expand().ToArray();
        Assert.All(runs, run => Assert.InRange(run.SizeBytes, 0, 64 * 1024));
        Assert.Contains(runs, run => run.ScenarioId == "execute.cancel");
        Assert.Contains(runs, run => run.ScenarioId == "parameter.substitute");
        Assert.Contains(runs, run => run.ScenarioId == "editor.paste");
        Assert.Equal(1, options.Repetitions);
    }

    [Fact]
    public void BaselineSelectsRepresentativeSizesAndMajorOperations()
    {
        var runs = RunnerOptions.Parse(["--mode", "baseline"]).Expand().ToArray();
        foreach (var id in new[] { "editor.paste", "editor.insert", "editor.typing.middle",
            "prepare.execution", "execute.direct", "recovery.persist" })
            Assert.Equal(new long[] { 1048576, 16777216, 67108864 },
                runs.Where(run => run.ScenarioId == id).Select(run => run.SizeBytes));
        Assert.All(runs, run => Assert.Equal("representative", run.Profile));
    }

    [Fact]
    public void ExtendedIncludesProfilesAndIntegratedLifecycleWithoutDuplicates()
    {
        var runs = RunnerOptions.Parse(["--mode", "extended"]).Expand().ToArray();
        foreach (var id in new[] { "parameter.substitute", "editor.navigation-scroll", "editor.selection-undo",
            "execute.integrated", "editor.paste-recovery", "execute.error", "execute.cancel", "editor.release" })
            Assert.Contains(runs, run => run.ScenarioId == id);
        foreach (var profile in ImportConfiguration.Profiles)
            Assert.Contains(runs, run => run.Profile == profile);
        Assert.Equal(runs.Length, runs.Distinct().Count());
        Assert.All(runs, ScenarioSettings.Validate);
    }

    [Fact]
    public void CoverageMakesUnrecordedRequiredWorkExplicit()
    {
        var coverage = ModeCoverage.Capture(RunnerOptions.Parse(["--mode", "smoke"]), []);
        Assert.NotEmpty(coverage);
        Assert.All(coverage, item =>
        {
            Assert.Equal("blocked", item.Outcome);
            Assert.Contains("recorded 0", item.Reason);
        });
        Assert.All(ModeCoverage.Limitations, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
    }

    [Theory]
    [InlineData("--mode", "unknown")]
    [InlineData("--mode", "baseline", "--scenarios", "harness.noop")]
    public void RejectsAmbiguousOrUnknownSelection(params string[] args) =>
        Assert.Throws<System.ArgumentException>(() => RunnerOptions.Parse(args));

    [Fact]
    public void ExplicitRepetitionsAndLegacyCustomSelectionRemainAvailable()
    {
        Assert.Equal(3, RunnerOptions.Parse(["--mode", "smoke", "--repetitions", "3"]).Repetitions);
        Assert.Equal(new RunSettings("harness.noop"),
            Assert.Single(RunnerOptions.Parse(["--scenarios", "harness.noop"]).Expand()));
    }

    [Fact]
    public void ExtendedWorkCanBeLimitedToOneProfileAndSize()
    {
        var options = RunnerOptions.Parse(["--scenarios", "variants.all", "--profile", "long-lines", "--size-mib", "16"]);
        var runs = options.Expand().ToArray();
        Assert.Equal(5, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal("long-lines", run.Profile);
            Assert.Equal(16 * 1024 * 1024, run.SizeBytes);
        });
        var coverage = ModeCoverage.Capture(options, []);
        Assert.Equal(5, coverage.Length);
        Assert.All(coverage, item => Assert.Equal("blocked", item.Outcome));
    }

    [Theory]
    [InlineData("--profile", "not-a-profile")]
    [InlineData("--size-mib", "0")]
    [InlineData("--scenario-id", "not-a-scenario")]
    public void RejectsFiltersThatSelectNoWorkloads(string option, string value) =>
        Assert.Throws<System.ArgumentException>(() => RunnerOptions.Parse(["--mode", "extended", option, value]));

    [Fact]
    public void FiltersCanSelectOneVariantWorkload()
    {
        var options = RunnerOptions.Parse(["--mode", "extended", "--scenario-id", "prepare.parameters",
            "--profile", "many-small-batches", "--size-mib", "64"]);
        Assert.Equal(new RunSettings("prepare.parameters", "many-small-batches", 64 * 1024 * 1024),
            Assert.Single(options.Expand()));
    }
}
