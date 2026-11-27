using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Model;

namespace DbConnect.Tests;

public sealed class TextMateBulkTokenTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BulkEditUndoRedoMatchesFreshHighlightingIncludingChangedSuffixState(bool insert, bool splitDelimiter)
    {
        const string line = "SELECT 'row';\r\n";
        var prefix = splitDelimiter ? "SELECT 0;\r" : "SELECT 0;\r\n" + string.Concat(Enumerable.Repeat(line, 24));
        var removed = splitDelimiter ? "" : string.Concat(Enumerable.Repeat(line, 48));
        var suffix = splitDelimiter ? "\nSELECT 9;" : string.Concat(Enumerable.Repeat(line, 24)) + "SELECT 9;";
        var original = prefix + removed + suffix;
        var replacement = splitDelimiter ? "/* open\r" : (insert ? string.Concat(Enumerable.Repeat("SELECT 2;\r\n", 48)) : "") + "/* open\r\n";
        var expected = prefix + replacement + (insert ? removed : "") + suffix;
        var editor = new TextEditor { Text = original };
        var errors = new List<Exception>();
        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, errors.Add);
        var tokens = new TMModel(model);
        using var completion = new TokenCompletion(tokens, model);
        var options = new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.LightPlus);
        var registry = new TextMateSharp.Registry.Registry(options);
        var grammar = registry.LoadGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".sql").Id));
        var first = model.Get(0);
        var last = model.Get(editor.Document.LineCount - 1);
        try
        {
            tokens.SetGrammar(grammar);
            await Verify(original);
            completion.ExpectChange();
            editor.Document.Replace(prefix.Length, insert ? 0 : removed.Length, replacement);
            await Verify(expected);
            completion.ExpectChange();
            editor.Undo();
            await Verify(original);
            completion.ExpectChange();
            editor.Redo();
            await Verify(expected);
        }
        finally { tokens.Dispose(); }

        async Task Verify(string expectedText)
        {
            await completion.WaitAsync();
            Assert.Empty(errors);
            Assert.Equal(expectedText, editor.Text);
            Assert.Equal(expectedText, model.DocumentSnapshot.GetText());
            Assert.Same(first, model.Get(0));
            Assert.Same(last, model.Get(editor.Document.LineCount - 1));
            var freshEditor = new TextEditor { Text = expectedText };
            using var freshModel = new TextEditorModel(freshEditor.TextArea.TextView, freshEditor.Document, error => throw error);
            var fresh = new TMModel(freshModel);
            using var freshCompletion = new TokenCompletion(fresh, freshModel);
            try
            {
                fresh.SetGrammar(grammar);
                await freshCompletion.WaitAsync();
                var actual = Signatures(tokens, editor.Document.LineCount);
                Assert.Equal(Signatures(fresh, freshEditor.Document.LineCount), actual);
                if (expectedText.Contains("/* open", StringComparison.Ordinal))
                    Assert.Contains("comment.block", actual[^1]);
                else
                    Assert.DoesNotContain("comment.block", actual[^1]);
            }
            finally { fresh.Dispose(); }
        }
    }

    private static string[] Signatures(TMModel model, int count) => Enumerable.Range(0, count)
        .Select(i => string.Join("|", model.GetLineTokens(i)?
            .Select(token => $"{token.StartIndex}:{string.Join(',', token.Scopes)}") ?? [])).ToArray();

    private sealed class TokenCompletion : IModelTokensChangedListener, IDisposable
    {
        private readonly TMModel _model;
        private readonly TextEditorModel _lines;
        private TaskCompletionSource _completed = CreateCompletion();

        public TokenCompletion(TMModel model, TextEditorModel lines)
        {
            _model = model;
            _lines = lines;
            model.AddModelTokensChangedListener(this);
        }

        public void ExpectChange() => Volatile.Write(ref _completed, CreateCompletion());
        public Task WaitAsync() => Volatile.Read(ref _completed).Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ModelTokensChanged(ModelTokensChangedEvent change)
        {
            var lastLineNumber = _lines.GetNumberOfLines();
            if (change.Ranges.Any(range => range.FromLineNumber <= lastLineNumber && range.ToLineNumber >= lastLineNumber))
                Volatile.Read(ref _completed).TrySetResult();
        }

        public void Dispose() => _model.RemoveModelTokensChangedListener(this);
        private static TaskCompletionSource CreateCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
