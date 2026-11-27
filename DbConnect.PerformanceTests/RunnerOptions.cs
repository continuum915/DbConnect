using System.Globalization;

namespace DbConnect.PerformanceTests;

public sealed record RunnerOptions(string Output, string[] Scenarios, int Repetitions, double TimeoutSeconds,
    int SamplingMilliseconds, string? Mode = null, string? Profile = null, long? SizeBytes = null,
    string? ScenarioId = null)
{
    public IEnumerable<RunSettings> Expand() => (Mode is null
        ? Scenarios.SelectMany(ScenarioSettings.Expand).Distinct() : ExecutionModes.Expand(Mode))
        .Where(settings => (ScenarioId is null || settings.ScenarioId == ScenarioId) &&
            (Profile is null || settings.Profile == Profile) &&
            (SizeBytes is null || settings.SizeBytes == SizeBytes));

    public static RunnerOptions Parse(string[] args)
    {
        var output = Path.Combine("artifacts", "performance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        string[] scenarios = ["harness.noop", "harness.idle"];
        var repetitions = 5;
        var timeout = 300d;
        var sampling = 20;
        string? mode = null;
        string? profile = null;
        long? sizeBytes = null;
        string? scenarioId = null;
        var explicitScenarios = false;
        var explicitRepetitions = false;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException("Every option requires a value.");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--output": output = value; break;
                case "--mode": mode = value; break;
                case "--scenarios": scenarios = value.Split(','); explicitScenarios = true; break;
                case "--repetitions": repetitions = int.Parse(value, CultureInfo.InvariantCulture); explicitRepetitions = true; break;
                case "--timeout-seconds": timeout = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--sampling-ms": sampling = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--profile": profile = value; break;
                case "--size-mib": sizeBytes = checked(long.Parse(value, CultureInfo.InvariantCulture) * RepresentativeImport.MiB); break;
                case "--scenario-id": scenarioId = value; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (mode is not null)
        {
            if (explicitScenarios) throw new ArgumentException("--mode and --scenarios are mutually exclusive.");
            scenarios = ExecutionModes.Scenarios(mode);
            if (mode == "smoke" && !explicitRepetitions) repetitions = 1;
        }
        if (repetitions < 1 || !double.IsFinite(timeout) || timeout <= 0 || sampling < 1)
            throw new ArgumentException("Repetitions, timeout, and sampling cadence must be positive.");
        if (sizeBytes is <= 0)
            throw new ArgumentException("The requested size must be a positive number of MiB.");
        scenarios = scenarios.SelectMany(id => id == "prepare.all" ? ScriptPreparationScenario.Ids : new[] { id }).ToArray();
        foreach (var scenario in scenarios)
            _ = ScenarioSettings.Expand(scenario).ToArray();
        var options = new RunnerOptions(output, scenarios, repetitions, timeout, sampling, mode, profile, sizeBytes, scenarioId);
        if (!options.Expand().Any())
            throw new ArgumentException("The requested selection contains no workloads.");
        return options;
    }
}
