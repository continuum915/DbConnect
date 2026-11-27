using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace SelectionAudit;

public static class LayerSupervisor
{
    public static async Task<int> RunAsync(string output, int size, string[]? layers = null)
    {
        if (size < 1 || size > 64) throw new ArgumentOutOfRangeException(nameof(size));
        if (layers != null && layers.Any(layer => layer is not ("bare" or "model" or "textmate" or "full")))
            throw new ArgumentException("Expected bare, model, textmate or full.", nameof(layers));
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"),
            JsonSerializer.Serialize(await DbConnect.PerformanceTests.EnvironmentMetadata.CaptureAsync(), new JsonSerializerOptions { WriteIndented = true }));
        foreach (var layer in layers ?? new[] { "bare", "model", "textmate", "full" })
        {
            var profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            using var process = new Process
            {
                StartInfo = new("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var arg in new[] { typeof(Program).Assembly.Location, "--layer-child", layer, size.ToString() })
                process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["DBCONNECT_DATA_DIRECTORY"] = profile;
            process.Start();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var kill = timeout.Token.Register(() => { if (!process.HasExited) process.Kill(true); });
            var messages = new List<JsonElement>();
            EventPipeSession? trace = null;
            FileStream? stream = null;
            Task? copying = null;
            var tracePath = Path.Combine(output, layer + ".nettrace");
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
                            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                                (long)ClrTraceEventParser.Keywords.GC)
                        ], requestRundown: true, circularBufferMB: 128);
                        stream = File.Create(tracePath); copying = trace.EventStream.CopyToAsync(stream);
                        await process.StandardInput.WriteLineAsync("go");
                    }
                    if (phase == "step") Console.WriteLine(layer + ": " + message.GetProperty("data"));
                    if (phase == "complete" && trace != null)
                    {
                        trace.Stop(); await copying!; await stream!.DisposeAsync(); trace.Dispose(); trace = null;
                    }
                }
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0 || !messages.Any(m => m.GetProperty("phase").GetString() == "complete"))
                    throw new InvalidDataException(await errors);
                TraceAnalysis.Analyze(tracePath, messages);
            }
            finally
            {
                trace?.Dispose(); if (stream != null) await stream.DisposeAsync();
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                await File.WriteAllTextAsync(Path.Combine(output, layer + "-raw.json"), JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(output, layer + "-stderr.txt"), await errors);
                if (Directory.Exists(profile)) Directory.Delete(profile, true);
            }
        }
        return 0;
    }
}
