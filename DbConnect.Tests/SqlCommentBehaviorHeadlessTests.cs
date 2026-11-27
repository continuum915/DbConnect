using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.Views;

namespace DbConnect.Tests;

public class SqlCommentBehaviorHeadlessTests
{
    [AvaloniaFact]
    public void ToggleWithoutSelection_CommentsOnlyTheCaretLine()
    {
        var editor = new TextEditor { Text = "select 1;\nselect 2;" };
        editor.TextArea.Caret.Line = 2;

        SqlEditorCommentBehavior.Toggle(editor);

        Assert.Equal("select 1;\n--select 2;", editor.Text);
        editor.Undo();
        Assert.Equal("select 1;\nselect 2;", editor.Text);
    }

    [AvaloniaFact]
    public void ToggleSelection_RemovesIndentedCommentsInOneUndoStep()
    {
        var editor = new TextEditor { Text = "  --select 1;\n--select 2;" };
        editor.Select(0, editor.Text.Length);

        SqlEditorCommentBehavior.Toggle(editor);

        Assert.Equal("  select 1;\nselect 2;", editor.Text);
        editor.Undo();
        Assert.Equal("  --select 1;\n--select 2;", editor.Text);
    }

    [AvaloniaFact]
    public void ToggleMixedSelection_AddsCommentsToEverySelectedLine()
    {
        var editor = new TextEditor { Text = "--select 1;\nselect 2;" };
        editor.Select(0, editor.Text.Length);

        SqlEditorCommentBehavior.Toggle(editor);

        Assert.Equal("----select 1;\n--select 2;", editor.Text);
    }
}
