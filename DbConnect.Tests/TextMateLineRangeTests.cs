using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;

namespace DbConnect.Tests;

public sealed class TextMateLineRangeTests
{
    [AvaloniaTheory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void MultilineReplacementUndoRedoPreservesUnchangedLineEntries(string newline)
    {
        var original = "before" + newline + "SELECT 1;" + newline + "after";
        var expected = "before" + newline + "SELECT 2;" + newline + "SELECT 3;" + newline + "after";
        var editor = new TextEditor { Text = original };
        var errors = new List<Exception>();
        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, errors.Add);
        var prefix = model.Get(0);
        var suffix = model.Get(2);
        var version = editor.Document.CreateSnapshot().Version;

        editor.Document.Replace(6 + newline.Length + 7, 1, "2;" + newline + "SELECT 3");
        Verify(expected, 4);
        editor.Undo();
        Verify(original, 3);
        editor.Redo();
        Verify(expected, 4);

        void Verify(string text, int lineCount)
        {
            Assert.Empty(errors);
            Assert.Equal(text, editor.Text);
            Assert.Equal(text, model.DocumentSnapshot.GetText());
            Assert.Equal(lineCount, model.GetSize());
            Assert.Equal(lineCount, model.DocumentSnapshot.LineCount);
            Assert.Same(prefix, model.Get(0));
            Assert.Same(suffix, model.Get(lineCount - 1));
            Assert.Null(model.Get(lineCount));
            Assert.True(editor.Document.CreateSnapshot().Version.CompareAge(version) > 0);
            version = editor.Document.CreateSnapshot().Version;
        }
    }
    [AvaloniaTheory]
    [InlineData("a\r\nb", 2, 0, "\r", "a\r\r\nb")]
    [InlineData("a\r\nb", 2, 0, "z\r", "a\rz\r\nb")]
    [InlineData("\r\nb", 1, 0, "\r", "\r\r\nb")]
    [InlineData("a\rX\nb", 2, 1, "", "a\r\nb")]
    [InlineData("a\r\nb", 1, 0, "\n", "a\n\r\nb")]
    [InlineData("a\r\nb", 1, 1, "", "a\nb")]
    [InlineData("a\r\nb", 2, 1, "", "a\rb")]
    public void DelimiterBoundaryEditsPreserveTheUnchangedSuffixThroughUndoRedo(
        string original, int offset, int length, string inserted, string expected)
    {
        var editor = new TextEditor { Text = original };
        using var model = new TextEditorModel(editor.TextArea.TextView, editor.Document, error => throw error);
        var suffix = model.Get(editor.Document.LineCount - 1);
        editor.Document.Replace(offset, length, inserted);
        Verify(expected);
        editor.Undo();
        Verify(original);
        editor.Redo();
        Verify(expected);

        void Verify(string text)
        {
            Assert.Equal(text, editor.Text);
            Assert.Equal(text, model.DocumentSnapshot.GetText());
            Assert.Same(suffix, model.Get(editor.Document.LineCount - 1));
            Assert.Null(model.Get(editor.Document.LineCount));
        }
    }
}
