using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.PerformanceTests;

public static class EditorAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DbConnect.App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
}

public static class EditorPasteChild
{
    public const string PasteId = "editor.paste";
    public const string InsertId = "editor.insert";
    public static bool IsTyping(string id) => id is "editor.typing.start" or "editor.typing.middle" or "editor.typing.end";
    public static bool IsEditor(string id) => IntegratedRunScenario.IsIntegrated(id) || id is PasteId or InsertId or EditorSelectionScenario.Id or EditorNavigationScenario.Id or EditorReleaseScenario.Id || IsTyping(id);

    public static async Task<int> RunAsync(RunSettings settings)
    {
        var lifetime = Stopwatch.StartNew();
        void Send(string phase, OperationMetrics? metrics = null, ImportMetadata? fixture = null, EditorObservations? editor = null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new ChildMessage(phase, metrics, lifetime.Elapsed.TotalMilliseconds,
                fixture, Editor: editor)));
            Console.Out.Flush();
        }

        var isolated = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(isolated) || !Path.IsPathFullyQualified(isolated))
            throw new InvalidOperationException("The parent must assign isolated persistence before UI initialization.");
        if (IntegratedRunScenario.IsIntegrated(settings.ScenarioId)) IntegratedRunScenario.VerifyIsolation(isolated);
        Send("initialized");
        var fixture = settings.Profile == MergeImportProxy.Profile
            ? MergeImportProxy.Generate(checked((int)settings.SizeBytes))
            : RepresentativeImport.Generate(checked((int)settings.SizeBytes), settings.Profile);
        Send("fixture-generated", fixture: fixture.Metadata);
        using var setup = IntegratedRunScenario.IsIntegrated(settings.ScenarioId) ? new SetupMemoryCapture(lifetime) : null;
        var startup = Stopwatch.StartNew();
        using var session = EditorSetupDiagnostics.Measure("headless-initialize", () => HeadlessUnitTestSession.StartNew(typeof(EditorAppBuilder)));
        if (IntegratedRunScenario.IsIntegrated(settings.ScenarioId))
            return await session.Dispatch(() => IntegratedRunScenario.RunAsync(settings, fixture, isolated, startup.Elapsed.TotalMilliseconds, Send, setup), CancellationToken.None);
        if (settings.ScenarioId == EditorReleaseScenario.Id)
            return await session.Dispatch(() => EditorReleaseScenario.RunAsync(fixture, isolated, startup.Elapsed.TotalMilliseconds, Send), CancellationToken.None);
        return await session.Dispatch(async () =>
        {
            var coldStartup = startup.Elapsed.TotalMilliseconds;
            if (AppDataPaths.RootDirectory != Path.GetFullPath(isolated))
                throw new InvalidOperationException("UI persistence isolation was not installed before initialization.");
            var warmup = Stopwatch.StartNew();
            var vm = new QueryTabViewModel(); // No workspace, main window, recovery timer or database session.
            var view = new QueryTabView { DataContext = vm };
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            try
            {
                window.Show();
                editor.Text = "SELECT 1;";
                window.UpdateLayout();
                using (var frame = window.CaptureRenderedFrame())
                {
                    if (frame == null) throw new InvalidOperationException("Skia headless rendering is unavailable.");
                }
                await Task.Delay(50);
                editor.Clear();
                editor.CaretOffset = 0;
                editor.Focus();
                window.UpdateLayout();
                if (settings.ScenarioId == EditorSelectionScenario.Id)
                    return await EditorSelectionScenario.RunAsync(window, editor, vm, fixture, isolated,
                        coldStartup, warmup, Send);
                if (settings.ScenarioId == EditorNavigationScenario.Id)
                    return await EditorNavigationScenario.RunAsync(window, editor, vm, fixture, isolated,
                        coldStartup, warmup, Send);
                var typing = IsTyping(settings.ScenarioId);
                var offset = settings.ScenarioId switch
                {
                    "editor.typing.middle" => fixture.Sql.Length / 2,
                    "editor.typing.end" => fixture.Sql.Length,
                    _ => 0
                };
                if (typing)
                {
                    editor.Text = fixture.Sql;
                    editor.CaretOffset = offset;
                    editor.TextArea.Focus();
                    window.UpdateLayout();
                    await Task.Delay(300);
                }
                var warmupMs = warmup.Elapsed.TotalMilliseconds;
                var clipboard = TopLevel.GetTopLevel(editor)?.Clipboard;
                if (settings.ScenarioId == PasteId)
                {
                    if (clipboard == null) throw new NotSupportedException("Headless clipboard unavailable; run editor.insert as a separately labeled fallback.");
                    await clipboard.SetTextAsync(fixture.Sql);
                    if (await clipboard.TryGetTextAsync() != fixture.Sql)
                        throw new NotSupportedException("Headless clipboard round trip failed; paste was not measured.");
                }
                Send("fixture-ready", fixture: fixture.Metadata);
                if (await Console.In.ReadLineAsync() != "go") return 24;

                var initialVersion = editor.Document.CreateSnapshot().Version;
                await using var probe = new DispatcherProbe();
                await probe.StartAsync();
                var inputCompleted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, EventArgs args)
                {
                    if (editor.Document.TextLength == fixture.Sql.Length + (typing ? 1 : 0)) inputCompleted.TrySetResult(Stopwatch.GetTimestamp());
                }
                editor.TextChanged += Changed;
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var start = Stopwatch.GetTimestamp();
                double synchronous;
                double completed;
                try
                {
                    if (typing) window.KeyTextInput("x");
                    else if (settings.ScenarioId == PasteId) editor.Paste();
                    else editor.Document.Insert(0, fixture.Sql);
                    synchronous = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Send("input-returned");
                    var insertedAt = await inputCompleted.Task;
                    completed = Stopwatch.GetElapsedTime(start, insertedAt).TotalMilliseconds;
                    Send("input-completed");
                }
                finally { editor.TextChanged -= Changed; }
                var (render, observationMs, layoutUpdates, dispatcher) = await EditorDeferredWork.ObserveAsync(
                    window, probe, start, () => Send("render-opportunity"));
                var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
                var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
                var heap = GC.GetTotalMemory(false);
                Send("observation-completed");

                // Content, binding and production TextMate checks happen outside operation counters.
                var expected = typing ? fixture.Sql.Insert(offset, "x") : fixture.Sql;
                if (typing && initialVersion.CompareAge(editor.Document.CreateSnapshot().Version) >= 0)
                    throw new InvalidDataException("Typing did not advance the document version.");
                if (editor.Text != expected || vm.QueryText != expected || editor.CaretOffset != (typing ? offset + 1 : fixture.Sql.Length))
                    throw new InvalidDataException("The real editor paste/insertion or view-model binding changed the document.");
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(editor.Text)));
                var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
                if (hash != expectedHash) throw new InvalidDataException("Document hash differs from the expected content.");
                var transformers = editor.TextArea.TextView.LineTransformers.Select(item => item.GetType().FullName ?? item.GetType().Name).ToArray();
                if (!transformers.Any(name => name.Contains("TextMate", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Production TextMate highlighting was not installed; this is not the primary baseline.");
                var observed = new EditorObservations(typing ? "headless-text-input" : settings.ScenarioId == PasteId ? "clipboard-paste" : "document-insert",
                    editor.Document.TextLength, hash, isolated, coldStartup, warmupMs, synchronous, completed,
                    render, observationMs, layoutUpdates, editor.TextArea.TextView.VisualLinesValid, false, transformers, dispatcher,
                    InputOffset: typing ? offset : null);
                Send("validated");
                var diagnostic = GC.GetTotalMemory(true);
                Send("complete", new(duration, allocation, deltas, heap, diagnostic), fixture.Metadata, observed);
                GC.KeepAlive(fixture);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                window.Close();
                window.Content = null;
                view.DataContext = null;
                await vm.ReleaseDbSessionAsync();
            }
        }, CancellationToken.None);
    }
}
