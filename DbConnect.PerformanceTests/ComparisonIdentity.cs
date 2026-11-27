using System.Globalization;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

internal sealed record ComparisonRun(int Index, JsonElement Raw, string ScenarioId,
    SortedDictionary<string, string> Identity, string[] Problems)
{
    public string Key => JsonSerializer.Serialize(Identity);
}

internal static class ComparisonIdentity
{
    public static ComparisonRun[] Read(JsonElement report)
    {
        if (!report.TryGetProperty("SchemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidDataException("Only measurement report SchemaVersion 1 is supported.");
        var runs = report.GetProperty("Runs").EnumerateArray().ToArray();
        return runs.Select((run, index) =>
        {
            var identity = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var problems = new List<string>();
            var settings = run.GetProperty("Settings");
            var scenario = settings.GetProperty("ScenarioId").GetString() ?? throw new InvalidDataException("Missing ScenarioId.");
            Add(identity, "Settings", settings);
            foreach (var field in new[] { "SamplingMilliseconds", "TimeoutSeconds" })
                Add(identity, field, report.GetProperty("Settings").GetProperty(field));
            Add(identity, "Policy", report.GetProperty("Policy"));
            identity["WarmupCount"] = runs.Count(x => Canonical(x.GetProperty("Settings")) == Canonical(settings) &&
                x.GetProperty("Warmup").GetBoolean()).ToString(CultureInfo.InvariantCulture);
            var fixture = At(run, "Fixture");
            Add(identity, "Fixture", fixture, "GenerationMilliseconds");
            if (settings.GetProperty("Profile").GetString() != "harness" && fixture.ValueKind != JsonValueKind.Object)
                problems.Add("Fixture identity unavailable; workload compatibility cannot be established.");
            if (fixture.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in new[] { "GeneratorVersion", "Seed", "Profile", "Sha256", "EncodedBytes", "Configuration" })
                    if (At(fixture, field).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        problems.Add($"Fixture.{field} identity unavailable.");
                if (At(fixture, "EncodedBytes").ValueKind == JsonValueKind.Number &&
                    At(fixture, "EncodedBytes").GetInt64() != settings.GetProperty("SizeBytes").GetInt64())
                    problems.Add("Fixture.EncodedBytes differs from Settings.SizeBytes.");
                if (At(fixture, "Profile").ValueKind == JsonValueKind.String &&
                    At(fixture, "Profile").GetString() != settings.GetProperty("Profile").GetString())
                    problems.Add("Fixture.Profile differs from Settings.Profile.");
            }
            var editor = At(run, "Editor");
            if (scenario.StartsWith("editor.", StringComparison.Ordinal) || scenario == IntegratedRunScenario.RunId)
            {
                var protocols = At(report, "EditorProtocols");
                var protocol = protocols.ValueKind == JsonValueKind.Object && protocols.TryGetProperty(scenario, out var configured)
                    ? configured : default;
                Add(identity, "EditorProtocol", protocol);
                foreach (var field in new[] { "Version", "DeferredObservationTargetMilliseconds" })
                    if (At(protocol, field).ValueKind != JsonValueKind.Number)
                        problems.Add($"EditorProtocol.{field} unavailable; legacy observation settings cannot establish compatibility.");
                if (editor.ValueKind != JsonValueKind.Object) problems.Add("Editor configuration unavailable.");
                foreach (var field in new[] { "InputMethod", "WindowWidth", "WindowHeight", "LineTransformers", "Dispatcher.CadenceMilliseconds" })
                    if (At(editor, field).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        problems.Add($"Editor.{field} configuration unavailable.");
                foreach (var field in new[] { "InputMethod", "WindowWidth", "WindowHeight", "InputOffset", "LineTransformers",
                    "InitialSelectionStart", "InitialSelectionLength", "Replacement", "Navigation", "Dispatcher.CadenceMilliseconds",
                    "Integrated.Operation", "Integrated.DeferredWork" })
                    Add(identity, "Editor." + field, At(editor, field));
                var operations = At(editor, "Operations");
                if (operations.ValueKind == JsonValueKind.Array)
                {
                    identity["Editor.Operations.Count"] = operations.GetArrayLength().ToString(CultureInfo.InvariantCulture);
                    foreach (var (operation, operationIndex) in operations.EnumerateArray().Select((x, i) => (x, i)))
                        foreach (var field in new[] { "Name", "InputMethod", "Dispatcher.CadenceMilliseconds" })
                            Add(identity, $"Editor.Operations[{operationIndex}].{field}", At(operation, field));
                }
            }
            if (At(run, "Recovery").ValueKind == JsonValueKind.Object)
                foreach (var field in new[] { "Attribution", "DriveType", "FileSystem", "WriteSemantics", "TabCount" })
                    Add(identity, "Recovery." + field, At(run, "Recovery." + field));
            if (At(run, "Stop").ValueKind == JsonValueKind.Object)
                foreach (var field in new[] { "ExpectedOutcome", "StopCommandNumber" })
                    Add(identity, "Stop." + field, At(run, "Stop." + field));
            return new ComparisonRun(index, run, scenario, identity, problems.ToArray());
        }).ToArray();
    }

    internal static JsonElement At(JsonElement root, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(segment, out root)) return default;
        }
        return root;
    }

    internal static void Add(SortedDictionary<string, string> target, string path, JsonElement value, string? omit = null)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                if (property.Name != omit) Add(target, path + "." + property.Name, property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            target[path + ".Count"] = value.GetArrayLength().ToString(CultureInfo.InvariantCulture);
            foreach (var (item, index) in value.EnumerateArray().Select((x, i) => (x, i))) Add(target, $"{path}[{index}]", item);
        }
        else target[path] = value.ValueKind switch
        {
            JsonValueKind.Undefined => "<missing>",
            JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            _ => value.GetRawText()
        };
    }

    private static string Canonical(JsonElement value)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Add(fields, "", value);
        return JsonSerializer.Serialize(fields);
    }
}
