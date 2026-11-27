using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.PerformanceTests;

public sealed record IntegratedRunObservations(string Operation, ExecutionObservations? Execution,
    double CommandMilliseconds, double ObservationMilliseconds, long? PersistedBytes,
    bool SnapshotVerified, string DeferredWork, QueryRunStageObservations[]? Stages = null,
    int? HighlightUpdates = null);

public static class IntegratedRunScenario
{
    public const string RunId = "execute.integrated";
    public const int RecoveryObservationMilliseconds = 2500;
    public const string RecoveryId = "editor.paste-recovery";
    public static bool IsIntegrated(string id) => id is RunId or RecoveryId;

    public static void VerifyIsolation(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
            Directory.EnumerateFileSystemEntries(directory).Any() || AppDataPaths.RootDirectory != Path.GetFullPath(directory))
            throw new InvalidOperationException("Integrated child requires a fresh, empty parent-assigned profile before app initialization.");
    }

    internal static async Task<int> RunAsync(RunSettings settings, ImportFixture fixture, string isolated,
        double startup, Action<string, OperationMetrics?, ImportMetadata?, EditorObservations?> send, SetupMemoryCapture? setup)
    {
        setup?.MarkUiReady();
        var warmup = Stopwatch.StartNew();
        var main = EditorSetupDiagnostics.Measure("workspace-construct", () => new MainWindowViewModel(new DatabaseService()));
        // Replace the initial workspace-backed tab: history persistence is outside the Run measurement.
        main.Tabs.Clear();
        var fake = new ImmediateDbSession();
        var tab = new QueryTabViewModel { Title = "Integrated measurement" };
        tab.SelectedSession = new DatabaseSession(new ConnectionInfo(), ImmediateSessionProvider.Create(fake));
        main.Tabs.Add(tab);
        main.SelectedTab = tab;
        var view = EditorSetupDiagnostics.Measure("view-construct", () => new QueryTabView { DataContext = tab });
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var window = EditorSetupDiagnostics.Measure("window-construct", () => new Window { Content = view, Width = 1000, Height = 700 });
        try
        {
            EditorSetupDiagnostics.Measure("window-show", window.Show);
            using (EditorSetupDiagnostics.Begin("warmup"))
            {
                editor.Text = "SELECT 1;";
                window.UpdateLayout();
                using (var frame = window.CaptureRenderedFrame())
                    if (frame == null) throw new InvalidOperationException("Skia rendering unavailable.");
                await Task.Delay(50);
                editor.Clear();
            }
            var clipboard = TopLevel.GetTopLevel(editor)?.Clipboard;
            if (settings.ScenarioId == RecoveryId)
            {
                if (clipboard == null) throw new NotSupportedException("Headless clipboard unavailable.");
                await clipboard.SetTextAsync(fixture.Sql);
                if (await clipboard.TryGetTextAsync() != fixture.Sql) throw new InvalidDataException("Clipboard round trip failed.");
            }
            else EditorSetupDiagnostics.Measure("editor-text-assignment", () => { editor.Text = fixture.Sql; });
            EditorSetupDiagnostics.Measure("explicit-layout", window.UpdateLayout);
            // Let setup recovery finish, then remove it so only measured edits can satisfy validation.
            var recoveryDirectory = Path.Combine(isolated, "recovery");
            await EditorSetupDiagnostics.MeasureAsync("recovery-settle", () =>
                RemoveCompletedSetupSnapshotAsync(recoveryDirectory,
                    settings.ScenarioId == RunId ? fixture.Sql : string.Empty));
            var transformers = editor.TextArea.TextView.LineTransformers.Select(x => x.GetType().FullName ?? x.GetType().Name).ToArray();
            if (!transformers.Any(x => x.Contains("TextMate", StringComparison.Ordinal))) throw new InvalidOperationException("Production TextMate missing.");
            var warmupMs = warmup.Elapsed.TotalMilliseconds;
            setup?.Stop();
            send("fixture-ready", null, fixture.Metadata, null);
            if (await Console.In.ReadLineAsync() != "go") return 24;
            using var runMemory = settings.ScenarioId == RunId ? setup?.CreateRunCapture() : null;
            await using var probe = new DispatcherProbe();
            await probe.StartAsync();
            var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var start = Stopwatch.GetTimestamp();
            probe.SetOperationOrigin(start);
            using var stages = settings.ScenarioId == RunId ? new QueryRunStageCapture(start) : null;
            fake.StartTimestamp = start;
            var pasted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, EventArgs args)
            {
                if (editor.Document.TextLength == fixture.Sql.Length) pasted.TrySetResult(Stopwatch.GetTimestamp());
            }
            double synchronous;
            double commandMs;
            var highlightUpdates = 0;
            void HighlightChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(tab.RunningStatementSpan) && tab.RunningStatementSpan != null)
                    highlightUpdates++;
            }
            tab.PropertyChanged += HighlightChanged;
            editor.TextChanged += Changed;
            try
            {
                send("command-starting", null, null, null);
                if (settings.ScenarioId == RunId) await main.ExecuteQueryCommand.ExecuteAsync(null);
                else editor.Paste();
                synchronous = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                commandMs = settings.ScenarioId == RunId ? synchronous :
                    Stopwatch.GetElapsedTime(start, await pasted.Task).TotalMilliseconds;
            }
            finally
            {
                editor.TextChanged -= Changed;
                tab.PropertyChanged -= HighlightChanged;
            }
            send("command-returned", null, null, null);
            var deferred = await EditorDeferredWork.ObserveAsync(window, probe, start, () => send("render-opportunity", null, null, null));
            // A fixed observation window, separate from paste latency; never force a snapshot write.
            if (settings.ScenarioId == RecoveryId) await Task.Delay(RecoveryObservationMilliseconds);
            var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
            var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
            var heap = GC.GetTotalMemory(false);
            runMemory?.Stop();
            send("observation-completed", null, null, null);

            if (editor.Text != fixture.Sql || tab.QueryText != fixture.Sql) throw new InvalidDataException("Editor/model content differs.");
            ExecutionObservations? execution = null;
            long? bytes = null;
            if (settings.ScenarioId == RunId)
            {
                execution = fixture.ProxyStatements is null
                    ? DirectBatchScenario.ValidateResults(fixture, fake, tab.Results, tab.CurrentResult!)
                    : MergeProxyValidation.Validate(fixture, fake, tab.Results, tab.CurrentResult!);
                if (main.IsExecuting || tab.IsExecuting || tab.SelectedResult != tab.CurrentResult || !tab.HasMultipleResults)
                    throw new InvalidDataException("Run did not present results or complete execution state.");
            }
            else
            {
                if (fake.CommandCount != 0 || tab.HasDbSession) throw new InvalidDataException("Paste opened a session.");
                var files = Directory.GetFiles(recoveryDirectory, "session-*.json");
                if (files.Length != 1) throw new InvalidDataException("Normal recovery scheduling did not produce one snapshot within observation.");
                bytes = new FileInfo(files[0]).Length;
                var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllTextAsync(files[0]));
                if (snapshot?.Pid != Environment.ProcessId || snapshot.Tabs.Count != 1 ||
                    snapshot.ActiveTabTitle != tab.Title || snapshot.Tabs[0].QueryText != fixture.Sql || snapshot.Tabs[0].Title != tab.Title)
                    throw new InvalidDataException("Persisted scheduled snapshot differs from the editor.");
            }
            var integrated = new IntegratedRunObservations(settings.ScenarioId, execution, commandMs, duration - commandMs,
                bytes, bytes.HasValue, "Normal recovery timer active; 2000 ms debounce is observation, not paste duration. Recovery observes an additional 2500 ms after the bounded render observation; no forced flush. TextMate completion unknown. History registration uses a tab without a history store; no history serialization measured. Fake returns affected-row results, not database throughput.", stages?.Snapshot(),
                settings.ScenarioId == RunId ? highlightUpdates : null);
            var observed = new EditorObservations(settings.ScenarioId == RunId ? "view-model-run" : "clipboard-paste-with-recovery",
                editor.Document.TextLength, fixture.Metadata.Sha256, isolated, startup, warmupMs, synchronous, commandMs,
                deferred.RenderOpportunityMilliseconds, duration - commandMs, deferred.LayoutUpdates, editor.TextArea.TextView.VisualLinesValid, false,
                transformers, deferred.Dispatcher, Integrated: integrated);
            send("validated", null, fixture.Metadata, observed);
            var diagnostic = GC.GetTotalMemory(true);
            send("complete", new(duration, allocation, deltas, heap, diagnostic), fixture.Metadata, observed);
            GC.KeepAlive(fixture);
            return 0;
        }
        finally
        {
            window.Close();
            window.Content = null;
            view.DataContext = null;
            await main.OnCleanExitAsync();
        }
    }
    private static async Task RemoveCompletedSetupSnapshotAsync(string directory, string expected)
    {
        // Parent deadline also bounds setup. Open exclusively to prove the timer writer closed.
        while (true)
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory, "session-*.json"))
                {
                    try
                    {
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            using var verification = EditorSetupDiagnostics.Begin("recovery-snapshot-verification");
                            if (!await Task.Run(() => RecoverySetupVerifier.Matches(stream, expected))) continue;
                        }
                        File.Delete(file);
                        return;
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
            }
            await Task.Delay(50);
        }
    }
}
