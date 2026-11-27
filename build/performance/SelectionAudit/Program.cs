using System.Text.Json;
using DbConnect.PerformanceTests;

namespace SelectionAudit;

public static class Program
{
    public static void Send(string phase, object? data = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { phase, data }));
        Console.Out.Flush();
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--analyze")
        {
            foreach (var trace in Directory.GetFiles(args[1], "*.nettrace"))
                TraceAnalysis.Analyze(trace, JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(trace.Replace(".nettrace", "-raw.json")))!);
            return 0;
        }
        if (args.Length is 3 or 4 && args[0] == "--layers") return await LayerSupervisor.RunAsync(args[1], int.Parse(args[2]), args.Length == 4 ? args[3].Split(',') : null);
        if (args.Length == 3 && args[0] == "--layer-child") return await LayerChild.RunAsync(args[1], int.Parse(args[2]));
        if (args.Length != 2 || args[0] != "--scale") throw new ArgumentException("Expected --scale output-directory, --layers output-directory size-MiB [layers], or --analyze output-directory.");
        Directory.CreateDirectory(args[1]);
        await File.WriteAllTextAsync(Path.Combine(args[1], "environment.json"),
            JsonSerializer.Serialize(await EnvironmentMetadata.CaptureAsync(), new JsonSerializerOptions { WriteIndented = true }));
        var runs = new List<RunRecord>();
        foreach (var size in new[] { 1, 2, 4 })
        {
            var run = await new ProcessSupervisor().RunAsync(new("editor.selection-undo", "representative", size * 1048576),
                1, false, TimeSpan.FromSeconds(30));
            runs.Add(run);
            await File.WriteAllTextAsync(Path.Combine(args[1], "scaling.json"),
                JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{size} MiB: {run.Outcome}; replace={run.Editor?.Operations?.SingleOrDefault(o => o.Name == "replace")?.SynchronousMilliseconds:0.###} ms");
            if (run.Outcome != "completed") return 2;
        }
        var ratio = runs[^1].Editor!.Operations!.Single(o => o.Name == "replace").SynchronousMilliseconds /
            runs[0].Editor!.Operations!.Single(o => o.Name == "replace").SynchronousMilliseconds;
        Console.WriteLine($"Fourfold input replacement ratio={ratio:0.###}; diagnostic superlinear-scaling signal if >6 (not a release timing gate).");
        return ratio > 6 ? 1 : 0;
    }
}
