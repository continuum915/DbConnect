using System.Globalization;

namespace DbConnect.PerformanceTests;

public static class ScenarioSettings
{
    public static IEnumerable<RunSettings> Expand(string scenario)
    {
        if (scenario == "execute.merge-proxy")
        {
            foreach (var size in new[] { 8, 16, 64 })
                yield return new(IntegratedRunScenario.RunId, MergeImportProxy.Profile, size * RepresentativeImport.MiB);
        }
        else if (scenario == "editor.smoke")
            yield return new(EditorPasteChild.PasteId, "representative", 64 * 1024);
        else if (scenario == "editor.variants")
        {
            foreach (var profile in new[] { "many-small-batches", "long-lines" })
                foreach (var size in new[] { 1, 16, 64 })
                    yield return new(EditorPasteChild.PasteId, profile, size * RepresentativeImport.MiB);
        }
        else if (EditorPasteChild.IsEditor(scenario))
        {
            foreach (var size in new[] { 1, 16, 64 })
                yield return new(scenario, "representative", size * RepresentativeImport.MiB);
        }
        else if (scenario == "variants.all")
        {
            foreach (var profile in ImportConfiguration.Profiles.Where(profile => profile != "representative"))
                foreach (var id in profile == "actual-parameters" ? ScriptPreparationScenario.ParameterIds : ScriptPreparationScenario.Ids)
                    foreach (var size in new[] { 1, 16, 64 })
                        yield return new(id, profile, size * RepresentativeImport.MiB);
        }
        else if (ScriptPreparationScenario.ParameterIds.Contains(scenario))
        {
            foreach (var size in new[] { 1, 16, 64 })
                yield return new(scenario, "actual-parameters", size * RepresentativeImport.MiB);
        }
        else if (ScriptPreparationScenario.Ids.Contains(scenario) || scenario is DirectBatchScenario.Id or RecoveryScenario.Id)
        {
            foreach (var size in new[] { 1, 16, 64 })
                yield return new(scenario, "representative", size * RepresentativeImport.MiB);
        }
        else
        {
            var settings = new RunSettings(scenario);
            Validate(settings);
            yield return settings;
        }
    }

    public static void Validate(RunSettings settings)
    {
        if (settings.ScenarioId == IntegratedRunScenario.RunId && settings.Profile == MergeImportProxy.Profile &&
            settings.SizeBytes >= 8L * RepresentativeImport.MiB && settings.SizeBytes <= 64L * RepresentativeImport.MiB) return;
        if (ExecutionStopChild.IsStop(settings.ScenarioId) && settings.Profile == "harness" && settings.SizeBytes == 0) return;
        if (IntegratedRunScenario.IsIntegrated(settings.ScenarioId) && settings.Profile != "representative")
            throw new ArgumentException("Integrated scenarios require the representative profile.");
        if (settings.Profile == "harness" && settings.SizeBytes == 0 &&
            settings.ScenarioId is "harness.noop" or "harness.idle" or "harness.hang" or "harness.crash")
            return;
        if (EditorPasteChild.IsEditor(settings.ScenarioId) && ImportConfiguration.Profiles.Contains(settings.Profile) &&
            settings.Profile != "actual-parameters" && settings.SizeBytes >= 8192 && settings.SizeBytes <= 64L * RepresentativeImport.MiB)
            return;
        if (EditorPasteChild.IsEditor(settings.ScenarioId) && settings.Profile == "representative" && settings.SizeBytes >= 1024 && settings.SizeBytes < 8192)
            return;
        if (ImportConfiguration.Profiles.Contains(settings.Profile) &&
            (settings.Profile == "actual-parameters" ? ScriptPreparationScenario.ParameterIds.Contains(settings.ScenarioId) : (ScriptPreparationScenario.Ids.Contains(settings.ScenarioId) || ((settings.ScenarioId is DirectBatchScenario.Id or RecoveryScenario.Id) && settings.Profile == "representative"))) &&
            settings.SizeBytes >= (settings.Profile == "long-lines" ? 8192 : 1024) && settings.SizeBytes <= 64L * RepresentativeImport.MiB)
            return;
        throw new ArgumentException("Unsupported scenario/profile/size combination.");
    }

    public static RunSettings ParseChild(string[] args) => new(args[1], args[2],
        long.Parse(args[3], CultureInfo.InvariantCulture));
}
