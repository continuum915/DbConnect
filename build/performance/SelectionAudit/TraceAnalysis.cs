using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace SelectionAudit;

public static class TraceAnalysis
{
    public static void Analyze(string path, List<JsonElement> messages)
    {
        using var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile(path));
        var pauses = new List<(long Start, long End, string Reason)>();
        long suspended = 0;
        var reason = string.Empty;
        using (var source = log.Events.GetSource())
        {
            // Absolute Windows QPC aligns EventPipe events with cross-process Stopwatch operation windows.
#pragma warning disable CS0618
            source.Clr.GCSuspendEEStart += data => { suspended = data.TimeStampQPC; reason = data.Reason.ToString(); };
            source.Clr.GCRestartEEStop += data =>
            {
                if (suspended != 0) pauses.Add((suspended, data.TimeStampQPC, reason));
                suspended = 0;
            };
#pragma warning restore CS0618
            source.Process();
        }
        var steps = messages.Where(m => m.GetProperty("phase").GetString() == "step")
            .Select(m => m.GetProperty("data")).ToArray();
        var records = new List<object>();
        foreach (var step in steps)
        {
            var start = step.GetProperty("started").GetInt64();
            var end = step.GetProperty("ended").GetInt64();
            var inclusive = new Dictionary<string, int>();
            var leaves = new Dictionary<string, int>();
            var operationSamples = 0;
            var allThreadSamples = 0;
#pragma warning disable CS0618
            foreach (var sample in log.Events.Where(e => e.ProviderName == "Microsoft-DotNETCore-SampleProfiler" && e.TimeStampQPC >= start && e.TimeStampQPC <= end))
#pragma warning restore CS0618
            {
                allThreadSamples++;
                var frames = new List<string>();
                var stack = sample.CallStack();
                while (stack != null)
                {
                    frames.Add(stack.CodeAddress.FullMethodName);
                    stack = stack.Caller;
                }
                // Exclude unrelated sleeping workers; these are sampled operation-thread stacks, not CPU percentages.
                if (!frames.Any(name => name.Contains("SelectionAudit.LayerChild", StringComparison.Ordinal) && name.Contains("Step", StringComparison.Ordinal))) continue;
                operationSamples++;
                leaves[frames[0]] = leaves.GetValueOrDefault(frames[0]) + 1;
                foreach (var name in frames.Distinct()) inclusive[name] = inclusive.GetValueOrDefault(name) + 1;
            }
            var suspensions = pauses.GroupBy(p => p.Reason).Select(group => new
            {
                Reason = group.Key,
                Milliseconds = group.Sum(p => Math.Max(0, Math.Min(end, p.End) - Math.Max(start, p.Start))) * 1000d / Stopwatch.Frequency
            }).Where(p => p.Milliseconds > 0).ToArray();
            records.Add(new
            {
                Operation = step.GetProperty("operation").GetString(),
                AllThreadSamples = allThreadSamples,
                OperationThreadSamples = operationSamples,
                Suspensions = suspensions,
                Inclusive = inclusive.OrderByDescending(p => p.Value).Take(100),
                Leaves = leaves.OrderByDescending(p => p.Value).Take(30)
            });
        }
        File.WriteAllText(path.Replace(".nettrace", "-analysis.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
}
