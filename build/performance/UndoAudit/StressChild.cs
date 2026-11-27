using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using DbConnect.PerformanceTests;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace UndoAudit;

public static class StressChild
{
    public static async Task RunAsync(int sizeMiB, int repetitions, string cadence)
    {
        if (cadence is not ("every-change" or "measured-only")) throw new ArgumentException("Unknown cadence");
        var data = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
        if (string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data)) throw new InvalidOperationException("Missing isolated profile");
        using var session = HeadlessUnitTestSession.StartNew(typeof(EditorAppBuilder));
        await session.Dispatch(async () =>
        {
            var fixture = RepresentativeImport.Generate(sizeMiB * 1048576);
            var sql = fixture.Sql;
            var offset = sql.IndexOf("Parents", sql.Length / 2, StringComparison.Ordinal) + 3;
            string[] variants = [sql, sql.Insert(offset, "x"), sql.Remove(offset, 1).Insert(offset, "q"), sql.Remove(offset, 1)];
            var oracles = variants.Select(s => SqlEditorStructure.Analyze(s)).ToArray();
            var records = new List<StepRecord>();
            var retained = new RetentionTracker();
            QueryTabViewModel? vm = new();
            QueryTabView? view = new() { DataContext = vm };
            TextEditor? editor = view.FindControl<TextEditor>("QueryEditor")!;
            Window? window = new() { Content = view, Width = 1000, Height = 700 };
            try
            {
                window.Show();
                // Keep validation fixtures from rooting the model string by object identity.
                editor.Text = new string(sql.AsSpan());
                window.UpdateLayout();
                await Task.Delay(100);
                var behavior = typeof(QueryTabView).GetField("_editorStructure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                var structureField = behavior.GetType().GetField("_structure", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var manager = (FoldingManager)behavior.GetType().GetField("_foldings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
                var initialTextAliasesFixture = ReferenceEquals(vm.QueryText, sql);
                if (initialTextAliasesFixture) throw new InvalidDataException("Initial model string aliases the validation fixture");
                Program.Send("ready", new { sizeMiB, repetitions, cadence, initialTextAliasesFixture, fixture.Metadata.Sha256, OsThreadId = GetCurrentThreadId(), System.Runtime.GCSettings.IsServerGC, LatencyMode = System.Runtime.GCSettings.LatencyMode.ToString() });
                if (await Console.In.ReadLineAsync() != "go") throw new InvalidDataException("Missing go handshake");
                var stressStart = Stopwatch.GetTimestamp();
                for (var cycle = 0; cycle <= repetitions; cycle++)
                {
                    editor.Document.UndoStack.ClearAll();
                    editor.TextArea.Focus(); editor.CaretOffset = offset;
                    await Step("type", () => window.KeyTextInput("x"), 1);
                    await Step("undo", () => editor.Undo(), 0);
                    await Step("redo", () => editor.Redo(), 1);
                    await Step("cleanup-redo", () => editor.Undo(), 0, true);
                    await Step("replace", () => editor.Document.Replace(offset, 1, "q"), 2);
                    await Step("cleanup-replace", () => editor.Undo(), 0, true);
                    await Step("delete", () => editor.Document.Remove(offset, 1), 3);
                    await Step("cleanup-delete", () => editor.Undo(), 0, true);

                    async Task Step(string name, Action action, int expectedIndex, bool cleanup = false)
                    {
                        var previous = new WeakReference(structureField.GetValue(behavior));
                        var collections = Counts();
                        var allocated = GC.GetTotalAllocatedBytes(true);
                        var start = Stopwatch.GetTimestamp();
                        action();
                        var end = Stopwatch.GetTimestamp();
                        var editBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                        var editGc = Delta(collections);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();
                        using (var frame = window.CaptureRenderedFrame())
                            if (frame == null) throw new InvalidDataException("Missing frame");
                        var render = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        await Task.Delay(60);
                        retained.TrackSnapshot(vm);
                        long consumerStart = 0, consumerEnd = 0, consumerBytes = 0;
                        int[] consumerGc = [0, 0, 0];
                        if (!cleanup || cadence == "every-change")
                        {
                            (consumerStart, consumerEnd, consumerBytes, consumerGc) = ReadAndTrack(vm, retained, variants[expectedIndex]);
                        }
                        Validate(editor, vm, manager, (SqlEditorStructure)structureField.GetValue(behavior)!, variants[expectedIndex], oracles[expectedIndex], offset);
                        if (!ReferenceEquals(previous.Target, structureField.GetValue(behavior))) throw new InvalidDataException("Unexpected full structure rebuild");
                        records.Add(new(cycle, name, cleanup, start, end, Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                            editBytes, editGc, consumerStart, consumerEnd,
                            consumerStart == 0 ? 0 : Stopwatch.GetElapsedTime(consumerStart, consumerEnd).TotalMilliseconds,
                            consumerBytes, consumerGc, Stopwatch.GetElapsedTime(start).TotalMilliseconds, render, true));
                    }
                }
                var stressEnd = Stopwatch.GetTimestamp();
                Program.Send("stress-complete", new { stressStart, stressEnd, records });
                if (await Console.In.ReadLineAsync() != "diagnose") throw new InvalidDataException("Missing diagnostic handshake");
                var open = retained.Diagnose(vm, variants);
                var viewReference = new WeakReference(view);
                var editorReference = new WeakReference(editor);
                window.Close(); window.Content = null; view.DataContext = null;
                // Clear explicit UI roots before the separate post-close diagnostic collections.
                window = null; view = null; editor = null; behavior = null; manager = null;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var closed = retained.Diagnose(vm, variants);
                Program.Send("diagnostics", new
                {
                    open,
                    closed,
                    ViewAlive = viewReference.IsAlive,
                    EditorAlive = editorReference.IsAlive,
                    Metadata = await EnvironmentMetadata.CaptureAsync(),
                    Policy = "Forced GC only after tracing/timing ends. Expected SQL/oracles remain retained. VM intentionally remains alive after view close. Weak identity counts exclude fixture/current values."
                });
                await vm.ReleaseDbSessionAsync();
                GC.KeepAlive(fixture); GC.KeepAlive(oracles);
                return 0;
            }
            finally { window?.Close(); if (view != null) view.DataContext = null; }
        }, CancellationToken.None);
    }

    private static int[] Counts() => Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    private static int[] Delta(int[] before) => Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - before[i]).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Start, long End, long Bytes, int[] Collections) ReadAndTrack(QueryTabViewModel vm, RetentionTracker retained, string expected)
    {
        var collections = Counts();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        var text = vm.QueryText;
        var end = Stopwatch.GetTimestamp();
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var deltas = Delta(collections);
        // Verification and weak tracking follow the consumer clock; no string is returned/retained by the caller.
        retained.Strings.Add(new WeakReference(text));
        if (text != expected || !ReferenceEquals(text, vm.QueryText)) throw new InvalidDataException("Current/cached SQL mismatch");
        return (start, end, bytes, deltas);
    }

    private static void Validate(TextEditor editor, QueryTabViewModel vm, FoldingManager manager, SqlEditorStructure actual,
        string expected, SqlEditorStructure oracle, int offset)
    {
        using var reader = editor.Document.CreateSnapshot().CreateReader();
        if (!ReaderEquals(reader, expected)) throw new InvalidDataException("Document content mismatch");
        if (!RetentionTracker.ModelEquals(vm, expected)) throw new InvalidDataException("Model content mismatch");
        if (!actual.Foldings.SequenceEqual(oracle.Foldings) ||
            !manager.AllFoldings.Select(f => (f.StartOffset, f.EndOffset)).SequenceEqual(oracle.Foldings.Select(f => (f.Start, f.End))))
            throw new InvalidDataException("Folding mismatch");
        foreach (var caret in new[] { 0, offset - 1, offset, offset + 1, expected.Length })
            if (actual.FindMatchingParentheses(caret) != oracle.FindMatchingParentheses(caret)) throw new InvalidDataException("Matching mismatch");
    }

    internal static bool ReaderEquals(TextReader reader, string expected)
    {
        var buffer = new char[4096];
        var offset = 0;
        int count;
        while ((count = reader.ReadBlock(buffer, 0, buffer.Length)) != 0)
        {
            if (offset + count > expected.Length || !expected.AsSpan(offset, count).SequenceEqual(buffer.AsSpan(0, count))) return false;
            offset += count;
        }
        return offset == expected.Length;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
