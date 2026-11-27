using System;
using System.Linq;
using AvaloniaEdit;

namespace DbConnect.Views;

/// <summary>Toggles comments across the selected lines as one undoable editor operation.</summary>
public static class SqlEditorCommentBehavior
{
    public static void Toggle(TextEditor editor)
    {
        var doc = editor.Document;
        var sel = editor.TextArea.Selection;
        int startLine = sel.IsEmpty
            ? editor.TextArea.Caret.Line
            : doc.GetLineByOffset(sel.SurroundingSegment.Offset).LineNumber;
        int endLine = sel.IsEmpty
            ? startLine
            : doc.GetLineByOffset(sel.SurroundingSegment.EndOffset).LineNumber;

        bool allCommented = Enumerable.Range(startLine, endLine - startLine + 1)
            .All(n =>
            {
                var line = doc.GetLineByNumber(n);
                var lineText = doc.GetText(line.Offset, line.Length);
                return lineText.TrimStart().StartsWith("--", StringComparison.Ordinal);
            });

        using var _ = doc.RunUpdate();
        for (int n = endLine; n >= startLine; n--)
        {
            var lineObj = doc.GetLineByNumber(n);
            var text = doc.GetText(lineObj.Offset, lineObj.Length);
            if (allCommented)
            {
                int idx = text.IndexOf("--", StringComparison.Ordinal);
                if (idx >= 0)
                    doc.Remove(lineObj.Offset + idx, 2);
            }
            else
            {
                doc.Insert(lineObj.Offset, "--");
            }
        }
    }
}
