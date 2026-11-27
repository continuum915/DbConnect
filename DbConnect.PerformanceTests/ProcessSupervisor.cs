using System.Diagnostics;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public sealed class ProcessSupervisor
{
    public async Task<RunRecord> RunAsync(RunSettings settings, int repetition, bool warmup, TimeSpan timeout,
        int samplingMilliseconds = 20)
    {
        if (timeout <= TimeSpan.Zero || samplingMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ScenarioSettings.Validate(settings);
        var samples = new List<MemorySample>();
        var phases = new List<PhaseTiming>();
        var setup = new List<SetupMemoryObservation>();
        var runMemory = new List<SetupMemoryObservation>();
        MemorySample? idleMemory = null;
        var phase = "not-started";
        OperationMetrics? metrics = null;
        ImportMetadata? fixture = null;
        PreparationObservations? observations = null;
        ExecutionObservations? execution = null;
        RecoveryObservations? recovery = null;
        EditorObservations? editor = null;
        ExecutionStopObservations? stop = null;
        var clock = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(typeof(ChildScenario).Assembly.Location);
        // Correctness tests exercise the protocol in their own build configuration.
        process.StartInfo.Environment["DBCONNECT_PERFORMANCE_ALLOW_DEBUG"] = "1";
        process.StartInfo.ArgumentList.Add("--child");
        process.StartInfo.ArgumentList.Add(settings.ScenarioId);
        process.StartInfo.ArgumentList.Add(settings.Profile);
        process.StartInfo.ArgumentList.Add(settings.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var editorDirectory = EditorPasteChild.IsEditor(settings.ScenarioId)
            ? Path.Combine(Path.GetTempPath(), "DbConnect-editor-performance-" + Guid.NewGuid().ToString("N")) : null;
        if (editorDirectory != null)
        {
            Directory.CreateDirectory(editorDirectory);
            process.StartInfo.Environment["DBCONNECT_DATA_DIRECTORY"] = editorDirectory;
        }
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            if (editorDirectory != null) Directory.Delete(editorDirectory, recursive: true);
            return new(settings, repetition, warmup, "launch-failed", null, phase, samples, null,
                error.ToString(), clock.Elapsed.TotalMilliseconds, EditorDataDirectory: editorDirectory);
        }
        using var deadline = new CancellationTokenSource(timeout);
        var stderr = process.StandardError.ReadToEndAsync();
        string? protocolFailure = null;
        var reading = ReadMessagesAsync();
        var exited = process.WaitForExitAsync();
        var timedOut = false;
        try
        {
            while (!exited.IsCompleted)
            {
                Sample();
                var nextSample = Task.Delay(samplingMilliseconds, deadline.Token);
                if (await Task.WhenAny(exited, nextSample) == exited)
                    break;
                await nextSample;
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = !process.HasExited;
            if (timedOut && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();
        await reading;
        var errorText = await stderr;
        var outcome = timedOut ? "timeout" : process.ExitCode != 0 ? "crashed" :
            metrics is null || phase != "complete" || protocolFailure is not null ? "failed" : "completed";
        if (editorDirectory != null) Directory.Delete(editorDirectory, recursive: true);
        return new(settings, repetition, warmup, outcome, process.ExitCode, phase, samples,
            outcome == "completed" ? metrics : null,
            outcome == "completed" ? null :
                $"{outcome}; exit={process.ExitCode}; last phase={phase}; timeout={timeout.TotalSeconds} s; {protocolFailure} {errorText}".Trim(),
            clock.Elapsed.TotalMilliseconds, idleMemory, phases, fixture, observations, execution, recovery, editor, editorDirectory, stop,
            setup.Count > 0 ? setup.ToArray() : null, runMemory.Count > 0 ? runMemory.ToArray() : null);

        void Sample()
        {
            try
            {
                lock (samples)
                {
                    process.Refresh();
                    samples.Add(new(clock.Elapsed.TotalMilliseconds, process.PrivateMemorySize64, process.WorkingSet64));
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { }
        }

        async Task ReadMessagesAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    var message = JsonSerializer.Deserialize<ChildMessage>(line)
                        ?? throw new InvalidDataException("Null child message.");
                    fixture = message.Fixture ?? fixture;
                    observations = message.Observations ?? observations;
                    execution = message.Execution ?? execution;
                    recovery = message.Recovery ?? recovery;
                    editor = message.Editor ?? editor;
                    stop = message.Stop ?? stop;
                    phase = message.Phase;
                    if (message.Setup != null) setup.Add(message.Setup);
                    if (message.RunMemory != null) runMemory.Add(message.RunMemory);
                    phases.Add(new(phase, clock.Elapsed.TotalMilliseconds, message.ChildElapsedMilliseconds));
                    if (message.Metrics is not null)
                        metrics = message.Metrics;
                    if (phase == "fixture-ready")
                    {
                        // Ready handshake guarantees a pre-operation idle sample even for a no-op.
                        lock (samples)
                        {
                            Sample();
                            idleMemory = samples.LastOrDefault();
                        }
                        await process.StandardInput.WriteLineAsync("go");
                        await process.StandardInput.FlushAsync();
                    }
                }
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
            {
                protocolFailure = error.Message;
            }
        }
    }
}
