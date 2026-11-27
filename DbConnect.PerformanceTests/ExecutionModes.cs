namespace DbConnect.PerformanceTests;

public static class ExecutionModes
{
    public static string[] Scenarios(string mode) => mode switch
    {
        "smoke" => ["harness.noop", "harness.idle", "prepare.execution", "parameter.substitute",
            "editor.paste", "execute.error", "execute.cancel"],
        "baseline" => [.. ScriptPreparationScenario.Ids, "editor.paste", "editor.insert",
            "editor.typing.middle", "execute.direct", "recovery.persist"],
        "extended" => [.. Scenarios("baseline"), "variants.all", "editor.variants",
            "editor.typing.start", "editor.typing.end", "editor.selection-undo", "editor.navigation-scroll",
            "execute.integrated", "editor.paste-recovery", "execute.error", "execute.cancel", "editor.release"],
        _ => throw new ArgumentException($"Unknown mode: {mode}. Expected smoke, baseline, or extended.")
    };

    public static IEnumerable<RunSettings> Expand(string mode) => Scenarios(mode)
        .SelectMany(ScenarioSettings.Expand)
        .Select(settings => mode == "smoke" && settings.SizeBytes > 0
            ? settings with { SizeBytes = 64 * 1024 } : settings)
        .Distinct();
}
