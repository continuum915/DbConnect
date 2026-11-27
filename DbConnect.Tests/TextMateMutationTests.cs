using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;

namespace DbConnect.Tests;

public class TextMateMutationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [AvaloniaFact]
    public void SingleLineDeletionDoesNotAllocateWholeDocumentLineArrays()
    {
        var editor = new TextEditor { Text = string.Concat(Enumerable.Repeat("SELECT CustomerName;\n", 100_000)) };
        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, error => throw error);
        var offset = editor.Document.GetLineByNumber(50_000).Offset + 10;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        editor.Document.Remove(offset, 1);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(editor.Document.LineCount, model.DocumentSnapshot.LineCount);
        Assert.Equal("SELECT CusomerName;\n", model.DocumentSnapshot.GetLineTextIncludingTerminator(49_999));
        output.WriteLine($"Single-character deletion allocated {allocated:N0} bytes.");
        Assert.True(allocated < 128 * 1024, $"One character deletion allocated {allocated:N0} bytes; expected less than 128 KiB.");
    }
    [AvaloniaFact]
    public void SnapshotReadsMatchDocumentAfterNewlineEditsAndUndoRedo()
    {
        string[] originals = ["alpha\nbeta\ngamma", "alpha\r\nbeta\r\ngamma\r\n", "a\rb\nc\r\nd", "\r\n\r\n", "", "last"];
        string[] inserts = ["", "x", "\n", "\r", "\r\n", "x\r\ny\n"];
        foreach (var text in originals)
            for (var offset = 0; offset <= text.Length; offset++)
                foreach (var length in new[] { 0, Math.Min(1, text.Length - offset), Math.Min(4, text.Length - offset), text.Length - offset }.Distinct())
                    foreach (var inserted in inserts)
                    {
                        var editor = new TextEditor { Text = text };
                        var errors = new List<Exception>();
                        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, errors.Add);
                        editor.Document.Replace(offset, length, inserted);
                        Verify();
                        editor.Undo();
                        Verify();
                        editor.Redo();
                        Verify();

                        void Verify()
                        {
                            Assert.Empty(errors);
                            var snapshot = model.DocumentSnapshot;
                            Assert.Equal(editor.Document.LineCount, snapshot.LineCount);
                            Assert.Equal(editor.Text, snapshot.GetText());
                            Assert.Null(model.Get(snapshot.LineCount));
                            for (var i = 0; i < snapshot.LineCount; i++)
                            {
                                Assert.NotNull(model.Get(i));
                                var line = editor.Document.GetLineByNumber(i + 1);
                                Assert.True(editor.Document.GetText(line.Offset, line.Length) == snapshot.GetLineText(i), $"Original={System.Text.Json.JsonSerializer.Serialize(text)} Offset={offset} Length={length} Insert={System.Text.Json.JsonSerializer.Serialize(inserted)} Current={System.Text.Json.JsonSerializer.Serialize(editor.Text)} Line={i}");
                                Assert.Equal(editor.Document.GetText(line.Offset, line.TotalLength), snapshot.GetLineTextIncludingTerminator(i));
                                Assert.Equal(line.Length, snapshot.GetLineLength(i));
                                Assert.Equal(line.TotalLength, snapshot.GetTotalLineLength(i));
                                Assert.Equal(editor.Document.GetText(line.EndOffset, line.DelimiterLength), snapshot.GetLineTerminator(i));
                                Assert.Equal(editor.Document.GetText(line.Offset, line.TotalLength), snapshot.GetLineTextIncludingTerminatorAsMemory(i).ToString());
                            }
                        }
                    }
    }
    [AvaloniaFact]
    public async Task TokenizationMatchesFreshModelAfterMultilineEditsAndBatchedUndoRedo()
    {
        var options = new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.LightPlus);
        var registry = new TextMateSharp.Registry.Registry(options);
        var grammar = registry.LoadGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".sql").Id));
        var editor = new TextEditor { Text = "SELECT 1;\r\n/* note */\r\nSELECT 'value';\r\n" };
        var errors = new List<Exception>();
        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, errors.Add);
        var tokens = new TextMateSharp.Model.TMModel(model);
        try
        {
            tokens.SetGrammar(grammar);
            await Verify();
            editor.Document.Remove(editor.Text.IndexOf("/*", StringComparison.Ordinal), 2);
            await Verify();
            editor.Undo();
            await Verify();
            editor.Redo();
            await Verify();
            using (editor.Document.RunUpdate())
            {
                editor.Document.Insert(6, "\r\n");
                editor.Document.Remove(0, 3);
                editor.Document.Insert(editor.Document.TextLength, "\r\nSELECT 42;");
            }
            await Verify();
            editor.Undo();
            await Verify();
            editor.Redo();
            await Verify();

        }
        finally { tokens.Dispose(); }

        async Task Verify()
        {
            Assert.Empty(errors);
            var oracleEditor = new TextEditor { Text = editor.Text };
            using var oracleModel = new TextEditorModel(oracleEditor.TextArea.TextView, oracleEditor.Document, error => throw error);
            var oracle = new TextMateSharp.Model.TMModel(oracleModel);
            try
            {
                oracle.SetGrammar(grammar);
                Assert.Equal(await Signatures(oracle, oracleEditor.Document.LineCount), await Signatures(tokens, editor.Document.LineCount));
            }
            finally { oracle.Dispose(); }
        }

        static async Task<string[]> Signatures(TextMateSharp.Model.TMModel tokenizer, int count)
        {
            tokenizer.ForceTokenization(0, count - 1);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (Enumerable.Range(0, count).Any(tokenizer.IsLineInvalid))
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(3), "Tokenization did not settle.");
                await Task.Delay(10);
                tokenizer.ForceTokenization(0, count - 1);
            }
            return Enumerable.Range(0, count).Select(i => string.Join("|", tokenizer.GetLineTokens(i)?.Select(t => $"{t.StartIndex}:{string.Join(',', t.Scopes)}") ?? [])).ToArray();
        }
    }
}