using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using DbConnect.PerformanceTests;
using DbConnect.ViewModels;
using DbConnect.Views;
using TextMateSharp.Grammars;

namespace SelectionAudit;

public static class LayerChild
{
    public static async Task<int> RunAsync(string layer, int size)
    {
        var isolated = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(isolated) || !Path.IsPathFullyQualified(isolated))
            throw new InvalidOperationException("The parent must assign isolated persistence before UI initialization.");
        if (size < 1 || size > 64 || layer is not ("bare" or "model" or "textmate" or "full"))
            throw new ArgumentException("Unsupported layer or size.");
        var fixture = RepresentativeImport.Generate(size * 1048576);
        using var session = HeadlessUnitTestSession.StartNew(typeof(EditorAppBuilder));
        return await session.Dispatch(async () =>
        {
            QueryTabViewModel? vm = layer == "full" ? new() : null;
            var view = vm == null ? null : new QueryTabView { DataContext = vm };
            var editor = view?.FindControl<TextEditor>("QueryEditor") ?? new TextEditor();
            var window = new Window { Content = (Control?)view ?? editor, Width = 1000, Height = 700 };
            TextMate.Installation? highlighting = null;
            TextEditorModel? lineModel = null;
            try
            {
                window.Show();
                if (layer == "textmate")
                {
                    var options = new RegistryOptions(ThemeName.LightPlus);
                    highlighting = editor.InstallTextMate(options);
                    highlighting.SetGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".sql").Id));
                }
                if (layer == "model") lineModel = new TextEditorModel(editor.TextArea.TextView, editor.Document, error => throw error);
                editor.Text = "SELECT 1;";
                window.UpdateLayout();
                using (var frame = window.CaptureRenderedFrame()) { }
                await Task.Delay(50);
                editor.Text = fixture.Sql;
                editor.Document.UndoStack.ClearAll();
                var offset = fixture.Sql.Length / 4;
                var length = fixture.Sql.Length / 2;
                editor.TextArea.Focus();
                editor.Select(offset, length);
                window.UpdateLayout();
                await Task.Delay(300);
                Program.Send("ready", new { layer, size, fixture.Metadata, offset, length });
                if (await Console.In.ReadLineAsync() != "go") return 24;
                var replaced = fixture.Sql[..offset] + "SELECT 2;" + fixture.Sql[(offset + length)..];
                await Step("replace", () => window.KeyTextInput("SELECT 2;"), replaced, offset + 9, 0);
                await Step("undo", () => editor.Undo(), fixture.Sql, offset, length);
                await Step("redo", () => editor.Redo(), replaced, offset, 9);
                Program.Send("complete");
                return 0;

                async Task Step(string operation, Action action, string expected, int selectedStart, int selectedLength)
                {
                    var before = editor.Document.CreateSnapshot().Version;
                    var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var started = Stopwatch.GetTimestamp();
                    action();
                    var ended = Stopwatch.GetTimestamp();
                    var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    var gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]).ToArray();
                    if (editor.Text != expected || vm != null && vm.QueryText != expected ||
                        editor.SelectionStart != selectedStart || editor.SelectionLength != selectedLength ||
                        editor.CaretOffset != offset + (operation == "undo" ? length : 9) ||
                        editor.Document.CreateSnapshot().Version.CompareAge(before) <= 0)
                        throw new InvalidDataException("Layer state validation failed: " + operation);
                    Program.Send("step", new { operation, started, ended, milliseconds = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds, bytes, gc, validated = true });
                    window.UpdateLayout();
                    using (var frame = window.CaptureRenderedFrame()) { }
                    await Task.Delay(60);
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                lineModel?.Dispose();
                highlighting?.Dispose();
                window.Close(); window.Content = null;
                if (view != null) view.DataContext = null;
                if (vm != null) await vm.ReleaseDbSessionAsync();
            }
        }, CancellationToken.None);
    }
}
