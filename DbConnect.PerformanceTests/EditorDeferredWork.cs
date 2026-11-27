using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record EditorDeferredObservations(double RenderOpportunityMilliseconds,
    double ObservationMilliseconds, int LayoutUpdates, DispatcherObservations Dispatcher);

public static class EditorDeferredWork
{
    public const int ObservationTargetMilliseconds = 300;
    public static async Task<EditorDeferredObservations> ObserveAsync(Window window, DispatcherProbe probe,
        long operationStarted, Action? rendered = null)
    {
        await QueryRunDiagnostics.MeasureAsync("render-queue-drain", () =>
            Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask());
        QueryRunDiagnostics.Measure("layout-render", () =>
        {
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            if (frame == null) throw new InvalidOperationException("No post-input rendered frame was observed.");
        });
        var render = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        rendered?.Invoke();
        var layouts = 0;
        void Layout(object? sender, EventArgs args) => layouts++;
        window.LayoutUpdated += Layout;
        var observation = Stopwatch.StartNew();
        try { await Task.Delay(ObservationTargetMilliseconds); }
        finally { window.LayoutUpdated -= Layout; }
        var observationMs = observation.Elapsed.TotalMilliseconds;
        var dispatcher = await probe.FinishAsync();
        return new(render, observationMs, layouts, dispatcher);
    }
}
