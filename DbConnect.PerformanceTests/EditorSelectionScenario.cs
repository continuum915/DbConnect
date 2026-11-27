using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using DbConnect.ViewModels;

namespace DbConnect.PerformanceTests;

public static class EditorSelectionScenario
{
    public const string Id = "editor.selection-undo";
    private const string Replacement = "SELECT 2;";

    public static async Task<int> RunAsync(Window window, TextEditor editor, QueryTabViewModel vm,
        ImportFixture fixture, string isolated, double coldStartup, Stopwatch warmup,
        Action<string, OperationMetrics?, ImportMetadata?, EditorObservations?> send)
    {
        var startOffset = fixture.Sql.Length / 4;
        var selectionLength = fixture.Sql.Length / 2;
        var replaced = string.Concat(fixture.Sql.AsSpan(0, startOffset), Replacement,
            fixture.Sql.AsSpan(startOffset + selectionLength));
        editor.Text = fixture.Sql;
        editor.Document.UndoStack.ClearAll();
        editor.CaretOffset = startOffset;
        editor.TextArea.Focus();
        window.UpdateLayout();
        await Task.Delay(300);
        var warmupMs = warmup.Elapsed.TotalMilliseconds;
        var transformers = editor.TextArea.TextView.LineTransformers
            .Select(item => item.GetType().FullName ?? item.GetType().Name).ToArray();
        if (!transformers.Any(name => name.Contains("TextMate", StringComparison.Ordinal)))
            throw new InvalidOperationException("Production TextMate highlighting was not installed.");
        Validate(fixture.Sql, startOffset, 0, startOffset);
        var operations = new List<EditorOperationObservations>();
        send("fixture-ready", null, fixture.Metadata, null);
        if (await Console.In.ReadLineAsync() != "go") return 24;

        await Step("select", "TextEditor.Select (programmatic range)",
            () => editor.Select(startOffset, selectionLength), fixture.Sql, startOffset, selectionLength,
            startOffset + selectionLength, false);
        await Step("replace", "Window.KeyTextInput (headless text input)",
            () => window.KeyTextInput(Replacement), replaced, startOffset + Replacement.Length, 0,
            startOffset + Replacement.Length, true);
        await Step("undo", "TextEditor.Undo (editor command API)",
            () => editor.Undo(), fixture.Sql, startOffset, selectionLength, startOffset + selectionLength, true);
        await Step("redo", "TextEditor.Redo (editor command API)",
            () => editor.Redo(), replaced, startOffset, Replacement.Length, startOffset + Replacement.Length, true);

        var final = operations[^1];
        var observed = Observations(final);
        var heap = final.PostOperationHeapBytes;
        send("validated", null, fixture.Metadata, observed);
        var diagnostic = GC.GetTotalMemory(true);
        send("complete", new(operations.Sum(item => item.DurationMilliseconds),
            operations.Sum(item => item.AllocatedBytes), Enumerable.Range(0, 3)
                .Select(i => operations.Sum(item => item.CollectionsByGeneration[i])).ToArray(), heap, diagnostic),
            fixture.Metadata, observed);
        GC.KeepAlive(fixture);
        GC.KeepAlive(replaced);
        return 0;

        EditorObservations Observations(EditorOperationObservations latest) => new(
            "selection-replacement-undo-redo", latest.DocumentCharacters, latest.DocumentSha256, isolated,
            coldStartup, warmupMs, operations.Sum(item => item.SynchronousMilliseconds),
            operations.Sum(item => item.SynchronousMilliseconds), operations.Sum(item => item.LayoutRenderOpportunityMilliseconds),
            operations.Sum(item => item.ObservationMilliseconds), operations.Sum(item => item.LayoutUpdatesDuringObservation),
            editor.TextArea.TextView.VisualLinesValid, false, transformers, latest.Dispatcher,
            Operations: operations.ToArray(), InitialSelectionStart: startOffset,
            InitialSelectionLength: selectionLength, Replacement: Replacement);

        async Task Step(string name, string inputMethod, Action action, string expected,
            int expectedStart, int expectedLength, int expectedCaret, bool changesDocument)
        {
            send(name + "-started", null, fixture.Metadata, null);
            var version = editor.Document.CreateSnapshot().Version;
            var measured = await EditorOperationMeasurement.MeasureAsync(window, action);
            var (render, observationMs, layouts, dispatcher) = measured.Deferred;

            // Exact content, selection, caret, model binding and version checks are outside all operation counters.
            var age = Math.Sign(editor.Document.CreateSnapshot().Version.CompareAge(version));
            if (age != (changesDocument ? 1 : 0))
                throw new InvalidDataException($"{name}: unexpected document version change {age}.");
            Validate(expected, expectedStart, expectedLength, expectedCaret);
            operations.Add(new(name, inputMethod, measured.SynchronousMilliseconds, render, observationMs, measured.DurationMilliseconds, measured.AllocatedBytes,
                measured.CollectionsByGeneration, layouts, dispatcher, editor.Document.TextLength, Hash(expected), editor.SelectionStart,
                editor.SelectionLength, editor.CaretOffset, age, measured.PostOperationHeapBytes));
            // Publish each successful step so a later timeout/failure retains its observations.
            send(name + "-validated", null, fixture.Metadata, Observations(operations[^1]));
        }

        void Validate(string expected, int expectedStart, int expectedLength, int expectedCaret)
        {
            if (editor.Text != expected || vm.QueryText != expected || editor.SelectionStart != expectedStart ||
                editor.SelectionLength != expectedLength || editor.CaretOffset != expectedCaret)
                throw new InvalidDataException($"Editor state differs: length={editor.Document.TextLength}, " +
                    $"selection={editor.SelectionStart}+{editor.SelectionLength}, caret={editor.CaretOffset}; " +
                    $"expected {expected.Length}, {expectedStart}+{expectedLength}, {expectedCaret}.");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
