using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.PerformanceTests;

public sealed record EditorReleaseMemory(long ManagedBytes, long PrivateBytes, long WorkingSetBytes,
    bool EditorAlive, bool ViewAlive, bool DocumentAlive, bool SnapshotAlive, bool ModelAlive);

public sealed record EditorReleaseObservations(bool RedoAvailableBeforeClose, bool Detached,
    EditorReleaseMemory Open, EditorReleaseMemory? Closed, DispatcherObservations Dispatcher,
    double CloseSynchronousMilliseconds, double DeferredMilliseconds);

public static class EditorReleaseScenario
{
    public const string Id = "editor.release";
    public const int CloseObservationMilliseconds = 300;

    public static async Task<int> RunAsync(ImportFixture fixture, string isolated, double coldStartup,
        Action<string, OperationMetrics?, ImportMetadata?, EditorObservations?> send)
    {
        var oracle = QueryTextSnapshot.FromString(fixture.Sql);
        var setup = await OpenAndCloseAsync(fixture, oracle, isolated, coldStartup, send);
        if (setup == null) return 24;
        // Run after the creating async method returned, so its UI locals are no longer needed.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(CloseObservationMilliseconds);
        var closed = Measure(setup.Targets);
        var release = new EditorReleaseObservations(true, setup.Detached, setup.Open, closed,
            setup.Dispatcher, setup.Synchronous, setup.Deferred);
        var observed = setup.Editor with { Release = release };
        send("release-diagnostic", null, fixture.Metadata, observed);
        send("complete", new(setup.Duration, setup.Allocated, setup.Collections, setup.Heap, closed.ManagedBytes), fixture.Metadata, observed);
        GC.KeepAlive(fixture);
        GC.KeepAlive(oracle);
        return 0;
    }

    private static async Task<ClosedSetup?> OpenAndCloseAsync(ImportFixture fixture, QueryTextSnapshot oracle,
        string isolated, double coldStartup, Action<string, OperationMetrics?, ImportMetadata?, EditorObservations?> send)
    {
        var warmup = Stopwatch.StartNew();
        var model = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = model };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        editor.Text = "SELECT 1;";
        window.UpdateLayout();
        using (var frame = window.CaptureRenderedFrame())
            if (frame == null) throw new InvalidOperationException("Headless rendering unavailable.");
        await Task.Delay(50);
        editor.Text = fixture.Sql;
        editor.Document.UndoStack.ClearAll();
        editor.CaretOffset = fixture.Sql.Length / 2;
        editor.TextArea.Focus();
        window.UpdateLayout();
        await Task.Delay(CloseObservationMilliseconds);
        var transformers = editor.TextArea.TextView.LineTransformers.Select(x => x.GetType().FullName ?? x.GetType().Name).ToArray();
        if (!transformers.Any(x => x.Contains("TextMate", StringComparison.Ordinal))) throw new InvalidOperationException("TextMate unavailable.");
        var warmupMs = warmup.Elapsed.TotalMilliseconds;
        send("fixture-ready", null, fixture.Metadata, null);
        if (await Console.In.ReadLineAsync() != "go") { window.Close(); return null; }

        // Edit and undo create real undo/redo history; validation stays outside measurement.
        var edit = await EditorOperationMeasurement.MeasureAsync(window, () => window.KeyTextInput("x"));
        editor.Undo();
        var document = editor.Document.CreateSnapshot();
        var documentSnapshot = new QueryTextSnapshot(document.TextLength, () => document.Text, document.CreateReader);
        if (!editor.Document.UndoStack.CanRedo || !model.CurrentQueryTextSnapshot.ContentEquals(oracle) ||
            !documentSnapshot.ContentEquals(oracle))
            throw new InvalidDataException("Edit/undo did not restore the model or retain redo history.");
        var snapshot = model.CurrentQueryTextSnapshot;
        var targets = new WeakTargets(new(editor), new(view), new(editor.Document), new(snapshot), new(model));
        var open = Measure(targets);
        var baseEditor = new EditorObservations("headless-edit-undo-close", fixture.Sql.Length, fixture.Metadata.Sha256,
            isolated, coldStartup, warmupMs, edit.SynchronousMilliseconds, edit.SynchronousMilliseconds,
            edit.Deferred.RenderOpportunityMilliseconds, edit.Deferred.ObservationMilliseconds,
            edit.Deferred.LayoutUpdates, editor.TextArea.TextView.VisualLinesValid, false, transformers, edit.Deferred.Dispatcher);
        send("open-diagnostic", null, fixture.Metadata, baseEditor with
        { Release = new(true, false, open, null, edit.Deferred.Dispatcher, 0, 0) });

        await using var probe = new DispatcherProbe();
        await probe.StartAsync();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        window.Close();
        window.Content = null;
        view.DataContext = null;
        var synchronous = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await model.ReleaseDbSessionAsync();
        var detached = window.Content == null && view.DataContext == null;
        if (!detached) throw new InvalidDataException("View was not detached.");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(CloseObservationMilliseconds);
        var dispatcher = await probe.FinishAsync();
        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
        var deltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        var heap = GC.GetTotalMemory(false);
        send("closed", null, fixture.Metadata, baseEditor with
        { Release = new(true, detached, open, null, dispatcher, synchronous, duration - synchronous) });
        return new(targets, open, detached, dispatcher, synchronous, duration - synchronous, duration, allocation, deltas, heap, baseEditor);
    }

    private static EditorReleaseMemory Measure(WeakTargets targets)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.WorkingSet64,
            targets.Editor.IsAlive, targets.View.IsAlive, targets.Document.IsAlive, targets.Snapshot.IsAlive, targets.Model.IsAlive);
    }

    private sealed record WeakTargets(WeakReference Editor, WeakReference View, WeakReference Document, WeakReference Snapshot, WeakReference Model);
    private sealed record ClosedSetup(WeakTargets Targets, EditorReleaseMemory Open, bool Detached, DispatcherObservations Dispatcher,
        double Synchronous, double Deferred, double Duration, long Allocated, int[] Collections, long Heap, EditorObservations Editor);
}
