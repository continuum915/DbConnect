using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace UndoAudit;

public static class Supervisor
{
    public static async Task RunAsync(string output, string baseline, string prototype, int blocks, int repetitions, string cadence)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var results = new List<object>();
        var targets = new[] { (Name: "baseline", Assembly: Path.GetFullPath(baseline)), (Name: "prototype", Assembly: Path.GetFullPath(prototype)) };
        for (var block = 0; block < blocks; block++)
        foreach (var target in block % 2 == 0 ? targets : targets.Reverse())
        {
            var label = $"{cadence}-{block}-{target.Name}";
            var traceFile = Path.Combine(output, label + ".nettrace");
            var profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            using var process = new Process { StartInfo = new("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            } };
            foreach (var arg in new[] { target.Assembly, "--child", "64", repetitions.ToString(), cadence }) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["DBCONNECT_DATA_DIRECTORY"] = profile;
            process.Start();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            using var kill = timeout.Token.Register(() => { if (!process.HasExited) process.Kill(true); });
            EventPipeSession? trace = null;
            FileStream? stream = null;
            Task? copying = null;
            var messages = new List<JsonElement>();
            List<StepRecord>? steps = null;
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    var message = json.RootElement.Clone(); messages.Add(message);
                    var phase = message.GetProperty("phase").GetString();
                    if (phase == "ready")
                    {
                        trace = new DiagnosticsClient(process.Id).StartEventPipeSession([
                            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GC)
                        ], requestRundown: true, circularBufferMB: 128);
                        stream = File.Create(traceFile);
                        copying = trace.EventStream.CopyToAsync(stream);
                        await process.StandardInput.WriteLineAsync("go");
                    }
                    if (phase == "stress-complete")
                    {
                        steps = message.GetProperty("data").GetProperty("records").Deserialize<List<StepRecord>>()!;
                        trace!.Stop(); await copying!; await stream!.DisposeAsync(); trace.Dispose(); trace = null;
                        await process.StandardInput.WriteLineAsync("diagnose");
                    }
                }
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0 || steps == null || !messages.Any(m => m.GetProperty("phase").GetString() == "diagnostics"))
                    throw new InvalidDataException(await errors);
                if (steps.Count != (repetitions + 1) * 8 || steps.Any(s => !s.Validated)) throw new InvalidDataException("Incomplete validation");
                await File.WriteAllTextAsync(Path.Combine(output, label + "-raw.json"), JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true }));
                var analysis = TraceSummary.Analyze(traceFile, steps);
                await File.WriteAllTextAsync(Path.Combine(output, label + "-analysis.json"), JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true }));
                results.Add(new { block, target.Name, Cadence = cadence, Raw = label + "-raw.json", Analysis = label + "-analysis.json", Outcome = "completed" });
                Console.WriteLine($"{label}: {steps.Count} validated operations");
            }
            finally
            {
                trace?.Dispose();
                if (stream != null) await stream.DisposeAsync();
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                // Exact GUID child of this audit's resolved output directory, created above.
                if (Directory.Exists(profile)) Directory.Delete(profile, true);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "runs.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
