using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace UndoAudit;

public static class TraceSummary
{
    public static void Reanalyze(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*-raw.json"))
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var message = json.RootElement.EnumerateArray().Single(m => m.GetProperty("phase").GetString() == "stress-complete");
            var steps = System.Text.Json.JsonSerializer.Deserialize<List<StepRecord>>(message.GetProperty("data").GetProperty("records"))!;
            var trace = file.Replace("-raw.json", ".nettrace");
            var result = Analyze(trace, steps);
            File.WriteAllText(file.Replace("-raw.json", "-analysis.json"), System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static object Analyze(string trace, IReadOnlyList<StepRecord> steps)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(trace);
        using var log = new TraceLog(etlx);
        using var source = log.Events.GetSource();
        var pauses = new List<Suspension>();
        var collections = new List<object>();
        long suspended = 0;
        var reason = string.Empty;
        // Absolute Windows QPC aligns trace events with child Stopwatch timestamps across processes.
#pragma warning disable CS0618
        source.Clr.GCSuspendEEStart += data => { suspended = data.TimeStampQPC; reason = data.Reason.ToString(); };
        source.Clr.GCRestartEEStop += data =>
        {
            if (suspended != 0) pauses.Add(new(suspended, data.TimeStampQPC, reason));
            suspended = 0;
        };
        source.Clr.GCStart += data => collections.Add(new { Qpc = data.TimeStampQPC, data.Count, data.Depth, Reason = data.Reason.ToString(), Type = data.Type.ToString() });
#pragma warning restore CS0618
        source.Process();
        double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        double Overlap(long start, long end) => start == 0 ? 0 : pauses.Sum(p => Ms(Math.Max(0, Math.Min(end, p.End) - Math.Max(start, p.Start))));
        return new
        {
            Definition = "CLR suspension-request start through restart complete; an end-to-end suspension interval, not CPU time. Windows QPC overlaps are clipped to each phase. Forced diagnostic GCs are outside this trace.",
            Pauses = pauses.Select(p => new { p.Start, p.End, p.Reason, Milliseconds = Ms(p.End - p.Start) }).ToArray(),
            Collections = collections,
            Steps = steps.Select(s => new
            {
                s.Cycle, s.Operation, s.Cleanup, s.EditMilliseconds, s.ConsumerMilliseconds, s.TotalMilliseconds,
                s.EditAllocatedBytes, s.ConsumerAllocatedBytes, s.EditCollections, s.ConsumerCollections,
                EditSuspensionMilliseconds = Overlap(s.EditStart, s.EditEnd),
                ConsumerSuspensionMilliseconds = Overlap(s.ConsumerStart, s.ConsumerEnd),
                s.Validated
            }).ToArray()
        };
    }
}
