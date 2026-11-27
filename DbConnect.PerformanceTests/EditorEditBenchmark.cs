using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.PerformanceTests;

public static class EditorEditBenchmark
{
    public static async Task RunAsync(string outputDirectory, bool smoke = false)
    {
        var isolated = Path.Combine(Path.GetTempPath(), "DbConnect-editor-edit-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY", isolated);
        Directory.CreateDirectory(isolated);
        Directory.CreateDirectory(outputDirectory);
        var sizes = smoke ? new[] { 1 } : new[] { 1, 16, 64 };
        var repetitions = smoke ? 1 : 5;
        try
        {
            var records = new List<object>();
            using var session = HeadlessUnitTestSession.StartNew(typeof(EditorAppBuilder));
            await session.Dispatch(async () =>
            {
                foreach (var size in sizes)
                {
                    var sql = RepresentativeImport.Generate(size * 1048576).Sql;
                    var offset = sql.IndexOf("Parents", sql.Length / 2, StringComparison.Ordinal) + 3;
                    var inserted = sql.Insert(offset, "x");
                    var replaced = sql.Remove(offset, 1).Insert(offset, "q");
                    var deleted = sql.Remove(offset, 1);
                    var expected = new Dictionary<string, SqlEditorStructure>();
                    foreach (var text in new[] { sql, inserted, replaced, deleted }) expected.Add(text, SqlEditorStructure.Analyze(text));
                    var vm = new QueryTabViewModel();
                    var view = new QueryTabView { DataContext = vm };
                    var editor = view.FindControl<TextEditor>("QueryEditor")!;
                    var window = new Window { Content = view, Width = 1000, Height = 700 };
                    try
                    {
                        window.Show();
                        editor.Text = sql;
                        window.UpdateLayout();
                        await Task.Delay(100);
                        var behavior = typeof(QueryTabView).GetField("_editorStructure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                        var tracker = behavior.GetType().GetField("_tracker", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var structure = behavior.GetType().GetField("_structure", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var manager = (FoldingManager)behavior.GetType().GetField("_foldings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
                        bool DocumentMatches(string value)
                        {
                            var source = editor.Document.CreateSnapshot();
                            return new QueryTextSnapshot(source.TextLength, () => source.Text, source.CreateReader)
                                .ContentEquals(QueryTextSnapshot.FromString(value));
                        }
                        for (var repetition = 0; repetition <= repetitions; repetition++)
                            foreach (var full in repetition % 2 == 0 ? new[] { true, false } : new[] { false, true })
                            {
                                editor.Document.UndoStack.ClearAll();
                                editor.TextArea.Focus();
                                editor.CaretOffset = offset;
                                await Step("type", () => window.KeyTextInput("x"), inserted);
                                await Step("undo", () => editor.Undo(), sql);
                                await Step("redo", () => editor.Redo(), inserted);
                                editor.Undo();
                                await Step("replace", () => editor.Document.Replace(offset, 1, "q"), replaced);
                                editor.Undo();
                                await Step("delete", () => editor.Document.Remove(offset, 1), deleted);
                                editor.Undo();
                                if (!DocumentMatches(sql)) throw new InvalidDataException("Cleanup did not restore baseline");

                                async Task Step(string operation, Action action, string text)
                                {
                                    if (full) tracker.SetValue(behavior, null); // Force the same production full-refresh fallback.
                                    var previous = new WeakReference<SqlEditorStructure>((SqlEditorStructure)structure.GetValue(behavior)!);
                                    await using var probe = new DispatcherProbe();
                                    await probe.StartAsync();
                                    var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                                    var before = GC.GetTotalAllocatedBytes(true);
                                    var start = Stopwatch.GetTimestamp();
                                    action();
                                    var synchronousMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                    var allocated = GC.GetTotalAllocatedBytes(true) - before;
                                    var gcDeltas = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
                                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                                    window.UpdateLayout();
                                    using (var frame = window.CaptureRenderedFrame())
                                        if (frame == null) throw new InvalidDataException("No rendered frame");
                                    var renderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                    await Task.Delay(60);
                                    var stalls = await probe.FinishAsync();
                                    var current = (SqlEditorStructure)structure.GetValue(behavior)!;
                                    var reused = previous.TryGetTarget(out var old) && ReferenceEquals(old, current);
                                    if (reused == full) throw new InvalidDataException("Unexpected full/incremental path: " + operation);
                                    // Validate snapshots without forcing a string after every simulated key.
                                    if (!DocumentMatches(text) || !vm.CurrentQueryTextSnapshot.ContentEquals(QueryTextSnapshot.FromString(text)))
                                        throw new InvalidDataException("Document/binding mismatch");
                                    double? firstConsumerMs = null;
                                    long? firstConsumerAllocated = null;
                                    double? cachedConsumerMs = null;
                                    if (operation == "delete")
                                    {
                                        // One explicit consumer probe per group, separately from edit timing.
                                        var consumerBefore = GC.GetTotalAllocatedBytes(true);
                                        var consumerStart = Stopwatch.GetTimestamp();
                                        var materialized = vm.QueryText;
                                        firstConsumerMs = Stopwatch.GetElapsedTime(consumerStart).TotalMilliseconds;
                                        firstConsumerAllocated = GC.GetTotalAllocatedBytes(true) - consumerBefore;
                                        consumerStart = Stopwatch.GetTimestamp();
                                        var cached = vm.QueryText;
                                        cachedConsumerMs = Stopwatch.GetElapsedTime(consumerStart).TotalMilliseconds;
                                        if (!ReferenceEquals(materialized, cached) || materialized != text)
                                            throw new InvalidDataException("Text cache/content mismatch");
                                    }
                                    var oracle = expected[text];
                                    if (!current.Foldings.SequenceEqual(oracle.Foldings)) throw new InvalidDataException("Fold oracle mismatch");
                                    if (!manager.AllFoldings.Select(f => (f.StartOffset, f.EndOffset)).SequenceEqual(oracle.Foldings.Select(f => (f.Start, f.End))))
                                        throw new InvalidDataException("Applied folds mismatch");
                                    foreach (var caret in new[] { 0, offset - 1, offset, offset + 1, text.Length })
                                        if (current.FindMatchingParentheses(caret) != oracle.FindMatchingParentheses(caret)) throw new InvalidDataException("Matching oracle mismatch");
                                    var record = new { size, repetition, full, operation, synchronousMs, renderMs, allocated, GcCollections = gcDeltas, FirstConsumerReadMilliseconds = firstConsumerMs, FirstConsumerAllocatedBytes = firstConsumerAllocated, CachedConsumerReadMilliseconds = cachedConsumerMs, stalls.MaximumDelayMilliseconds, stalls.SampleCount, stalls.MissedIntervals, Dispatcher = stalls, reused, validated = true };
                                    records.Add(record);
                                    Console.WriteLine(JsonSerializer.Serialize(record));
                                }
                            }
                    }
                    finally { window.Close(); view.DataContext = null; await vm.ReleaseDbSessionAsync(); }
                }
                return 0;
            }, CancellationToken.None);
            if (records.Count != sizes.Length * (repetitions + 1) * 10) throw new InvalidDataException("Incomplete edit benchmark");
            var metadata = await EnvironmentMetadata.CaptureAsync();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(new
            {
                Metadata = metadata,
                Policy = "Same warmed real query editor; type uses headless text input, other edits use document/undo commands; modes alternate order each repetition. Repetition zero is warm-up. Full=true forces the production full-refresh fallback via reflection. Exact document text is compared through readers; the model snapshot and precomputed full-analysis fold/matching oracles are checked outside edit timing without forcing strings per key. One explicit model-string consumer probe follows each delete (the last operation in a group); first-read time/allocation and cached-read time are recorded separately: lazy text defers this cost, it does not eliminate required full-text reads. Allocations are process-wide synchronous-window estimates, not retained/peak memory. A render opportunity and fixed 60 ms observation do not establish completed highlighting. Dispatcher samples are sparse: raw measurements/max only, no percentile or operation-p95 claim.",
                Records = records
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (Directory.Exists(isolated)) Directory.Delete(isolated, recursive: true); }
    }
}
