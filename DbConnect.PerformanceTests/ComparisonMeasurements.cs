using System.Text.Json;

namespace DbConnect.PerformanceTests;

internal sealed record ComparisonValue(double? Value, string Unit, int? DispatcherSamples = null);

internal static class ComparisonMeasurements
{
    public static Dictionary<string, ComparisonValue> Read(JsonElement run)
    {
        var result = new Dictionary<string, ComparisonValue>(StringComparer.Ordinal);
        foreach (var field in new[] { "Metrics", "Execution", "Editor", "Recovery", "Stop" })
            Visit(ComparisonIdentity.At(run, field), field, result);
        foreach (var field in new[] { "PrivateBytes", "WorkingSetBytes" })
        {
            var idle = ComparisonIdentity.At(run, "IdleMemory." + field);
            if (Number(idle) is { } idleValue) result["IdleMemory." + field] = new(idleValue, "bytes");
            var samples = ComparisonIdentity.At(run, "MemorySamples");
            if (samples.ValueKind == JsonValueKind.Array)
            {
                var values = samples.EnumerateArray().Select(x => Number(ComparisonIdentity.At(x, field))).OfType<double>().ToArray();
                if (values.Length > 0) result["Peak." + field] = new(values.Max(), "bytes");
            }
        }
        return result;
    }

    public static bool Eligible(JsonElement run) => !run.GetProperty("Warmup").GetBoolean() &&
        run.GetProperty("Outcome").GetString() == "completed";

    private static void Visit(JsonElement value, string path, Dictionary<string, ComparisonValue> result)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            var name = path + "." + property.Name;
            if (property.Name == "Dispatcher") { Dispatcher(property.Value, name, result); continue; }
            // Integrated retention fields are explicitly unmeasured in schema 1.
            if (name == "Editor.Integrated.Execution") continue;
            if (property.Value.ValueKind == JsonValueKind.Object) Visit(property.Value, name, result);
            else if (property.Name == "Operations" && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var (operation, index) in property.Value.EnumerateArray().Select((x, i) => (x, i)))
                    Visit(operation, $"{name}[{index}]", result);
            else if (property.Name == "CollectionsByGeneration" && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var (count, index) in property.Value.EnumerateArray().Select((x, i) => (x, i)))
                    result[$"{name}[{index}]"] = new(Number(count), "collections");
            else if (property.Name.EndsWith("Milliseconds", StringComparison.Ordinal)) result[name] = new(Number(property.Value), "ms");
            else if (property.Name.EndsWith("Bytes", StringComparison.Ordinal)) result[name] = new(Number(property.Value), "bytes");
        }
    }

    private static void Dispatcher(JsonElement dispatcher, string path, Dictionary<string, ComparisonValue> result)
    {
        if (dispatcher.ValueKind != JsonValueKind.Object) return;
        var count = ComparisonIdentity.At(dispatcher, "SampleCount");
        var sampleCount = count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number) ? number : 0;
        result[path + ".MaximumDelayMilliseconds"] = new(Number(ComparisonIdentity.At(dispatcher, "MaximumDelayMilliseconds")), "ms", sampleCount);
        result[path + ".MaximumCadenceDelayMilliseconds"] = new(Number(ComparisonIdentity.At(dispatcher, "MaximumCadenceDelayMilliseconds")), "ms", sampleCount);
        var delays = ComparisonIdentity.At(dispatcher, "QueueDelaysMilliseconds");
        var values = delays.ValueKind == JsonValueKind.Array
            ? delays.EnumerateArray().Select(Number).OfType<double>().Where(x => x >= 0).Order().ToArray() : [];
        var sufficient = sampleCount >= 20 && delays.ValueKind == JsonValueKind.Array &&
            delays.GetArrayLength() == sampleCount && values.Length == sampleCount;
        foreach (var (label, fraction) in new[] { ("P50Milliseconds", .5), ("P95Milliseconds", .95) })
            result[path + "." + label] = new(sufficient ? values[(int)Math.Ceiling(fraction * values.Length) - 1] : null, "ms", sampleCount);
    }

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
