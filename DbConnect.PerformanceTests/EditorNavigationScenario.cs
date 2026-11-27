using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.PerformanceTests;

public static class EditorNavigationScenario
{
    public const string Id = "editor.navigation-scroll";

    public static async Task<int> RunAsync(Window window, TextEditor editor, QueryTabViewModel vm,
        ImportFixture fixture, string isolated, double coldStartup, Stopwatch warmup,
        Action<string, OperationMetrics?, ImportMetadata?, EditorObservations?> send)
    {
        var middle = fixture.Sql.LastIndexOf('\n', fixture.Sql.Length / 2 - 1) + 1;
        var settings = new EditorNavigationSettings(middle, fixture.Sql.AsSpan(0, middle).Count('\n') + 1,
            fixture.Sql.Length, fixture.Metadata.Lines, 1, 3, editor.WordWrap);
        editor.Text = fixture.Sql;
        editor.Document.UndoStack.ClearAll();
        editor.CaretOffset = 0;
        editor.TextArea.Focus();
        window.UpdateLayout();
        editor.ScrollToHome();
        await Task.Delay(300);
        window.UpdateLayout();
        var textView = editor.TextArea.TextView;
        var scroll = (IScrollable)textView;
        var viewer = editor.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(item => ReferenceEquals(item.Content, editor.TextArea));
        var initial = State();
        if (initial.Height <= 0 || initial.Width <= 0 || initial.Y != 0)
            throw new InvalidDataException("The initial text viewport is unavailable or not at the top.");
        var transformers = textView.LineTransformers.Select(item => item.GetType().FullName ?? item.GetType().Name).ToArray();
        if (!transformers.Any(name => name.Contains("TextMate", StringComparison.Ordinal)))
            throw new InvalidOperationException("Production TextMate highlighting was not installed.");
        settings = settings with { InitialViewport = initial };
        var version = editor.Document.CreateSnapshot().Version;
        var oracle = QueryTextSnapshot.FromString(fixture.Sql);
        ValidateDocument();
        var warmupMs = warmup.Elapsed.TotalMilliseconds;
        var operations = new List<EditorOperationObservations>();
        send("fixture-ready", null, fixture.Metadata, Observations());
        if (await Console.In.ReadLineAsync() != "go") return 24;

        await Navigate("navigate-middle", settings.MiddleOffset, settings.MiddleLine);
        for (var step = 1; step <= settings.ScrollStepsPerDirection; step++) await Scroll("scroll-down-" + step, 1);
        for (var step = 1; step <= settings.ScrollStepsPerDirection; step++) await Scroll("scroll-up-" + step, -1);
        await Navigate("navigate-end", settings.EndOffset, settings.EndLine);
        await Scroll("scroll-up-from-end", -1);
        await Scroll("scroll-down-to-end", 1);

        var observed = Observations();
        var final = operations[^1];
        send("validated", null, fixture.Metadata, observed);
        var diagnostic = GC.GetTotalMemory(true);
        send("complete", new(operations.Sum(item => item.DurationMilliseconds), operations.Sum(item => item.AllocatedBytes),
            Enumerable.Range(0, 3).Select(i => operations.Sum(item => item.CollectionsByGeneration[i])).ToArray(),
            final.PostOperationHeapBytes, diagnostic), fixture.Metadata, observed);
        GC.KeepAlive(fixture);
        GC.KeepAlive(oracle);
        return 0;

        Task Navigate(string name, int offset, int line) => Step(name, "TextEditor.CaretOffset + ScrollToLine (programmatic)",
            () => { editor.CaretOffset = offset; editor.ScrollToLine(line); }, offset, line, null, null);

        Task Scroll(string name, int direction)
        {
            var before = State();
            var requested = before.Y + direction * settings.ViewportsPerScroll * before.Height;
            var target = Math.Clamp(requested, 0, Math.Max(0, before.ExtentHeight - before.Height));
            return Step(name, "ScrollViewer.Offset (programmatic viewport scroll)",
                () => viewer.Offset = new Vector(viewer.Offset.X, target), editor.CaretOffset, null, requested, target);
        }

        async Task Step(string name, string inputMethod, Action action, int caret, int? targetLine, double? requestedY, double? targetY)
        {
            send(name + "-started", null, fixture.Metadata, null);
            var before = State();
            var measured = await EditorOperationMeasurement.MeasureAsync(window, action);
            var after = State();
            ValidateDocument();
            if (editor.CaretOffset != caret || editor.SelectionLength != 0 || !after.VisualLinesValid)
                throw new InvalidDataException(name + ": unexpected caret, selection or visual-line state.");
            if (targetLine is { } line && (line < after.FirstVisibleLine || line > after.LastVisibleLine))
                throw new InvalidDataException(name + ": navigation target is not in the observed viewport.");
            if (targetY is { } y && Math.Abs(after.Y - y) > 1)
                throw new InvalidDataException($"{name}: scroll offset {after.Y} differs from clamped request {y}.");
            var (render, observationMs, layouts, dispatcher) = measured.Deferred;
            operations.Add(new(name, inputMethod, measured.SynchronousMilliseconds, render, observationMs,
                measured.DurationMilliseconds, measured.AllocatedBytes, measured.CollectionsByGeneration, layouts, dispatcher,
                editor.Document.TextLength, fixture.Metadata.Sha256, editor.SelectionStart, editor.SelectionLength, editor.CaretOffset,
                0, measured.PostOperationHeapBytes, new(before, after, targetLine, requestedY, targetY)));
            send(name + "-validated", null, fixture.Metadata, Observations());
        }

        EditorViewportState State()
        {
            var valid = textView.VisualLinesValid;
            var lines = valid ? textView.VisualLines : null;
            return new(scroll.Offset.X, scroll.Offset.Y, scroll.Viewport.Width, scroll.Viewport.Height, scroll.Extent.Height,
                lines?.FirstOrDefault()?.FirstDocumentLine.LineNumber ?? 0,
                lines?.LastOrDefault()?.LastDocumentLine.LineNumber ?? 0, valid);
        }

        void ValidateDocument()
        {
            var source = editor.Document.CreateSnapshot();
            var actual = new QueryTextSnapshot(source.TextLength, () => source.Text, source.CreateReader);
            if (source.Version.CompareAge(version) != 0 || !actual.ContentEquals(oracle) || !vm.CurrentQueryTextSnapshot.ContentEquals(oracle))
                throw new InvalidDataException("Navigation/scrolling changed the document, version or model content.");
        }

        EditorObservations Observations() => new("programmatic-navigation-and-viewport-scroll", fixture.Sql.Length,
            fixture.Metadata.Sha256, isolated, coldStartup, warmupMs,
            operations.Sum(item => item.SynchronousMilliseconds), operations.Sum(item => item.SynchronousMilliseconds),
            operations.Sum(item => item.LayoutRenderOpportunityMilliseconds), operations.Sum(item => item.ObservationMilliseconds),
            operations.Sum(item => item.LayoutUpdatesDuringObservation), textView.VisualLinesValid, false, transformers,
            operations.LastOrDefault()?.Dispatcher ?? new(20, 0, 0, 0, null, null, []), Operations: operations.ToArray(), Navigation: settings);
    }
}
