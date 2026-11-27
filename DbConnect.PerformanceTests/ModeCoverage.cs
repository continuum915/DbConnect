namespace DbConnect.PerformanceTests;

public sealed record WorkloadCoverage(RunSettings Settings, string Outcome, string? Reason,
    int ExpectedRuns, int RecordedRuns, string[] Outcomes);
public sealed record UnsupportedMeasurement(string Measurement, string Outcome, string Reason);

public static class ModeCoverage
{
    public static WorkloadCoverage[] Capture(RunnerOptions options, IReadOnlyList<RunRecord> records) =>
        options.Expand().Select(settings =>
        {
            var runs = records.Where(run => run.Settings == settings).ToArray();
            var expected = options.Repetitions + 1;
            var complete = runs.Length == expected;
            var successful = complete && runs.All(run => run.Outcome == "completed");
            var reason = !complete ? $"Expected {expected} children including warm-up; recorded {runs.Length}." :
                successful ? null : string.Join("; ", runs.Where(run => run.Outcome != "completed")
                    .Select(run => run.Failure ?? $"{run.Outcome} at {run.LastCompletedPhase}"));
            return new WorkloadCoverage(settings, successful ? "completed" : complete ? "failed" : "blocked",
                reason, expected, runs.Length, runs.Select(run => run.Outcome).ToArray());
        }).ToArray();

    public static UnsupportedMeasurement[] Limitations { get; } =
    [
        new("Native input/display latency", "unsupported", "Avalonia headless uses synthetic input and render opportunities; no physical display or OS clipboard is measured."),
        new("Completed background highlighting", "unsupported", "Bounded dispatcher/render observation cannot establish that all highlighting has completed."),
        new("Real database/server interruption", "unsupported", "Execution uses deterministic fake sessions; cancellation proves orchestration only.")
    ];
}
