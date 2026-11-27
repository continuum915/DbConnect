using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class IncrementalEditorHeadlessTests
{
    [AvaloniaTheory]
    [InlineData("'")]
    [InlineData("/*")]
    [InlineData("--")]
    [InlineData("\n")]
    [InlineData("(")]
    [InlineData(")")]
    public void TokenInteriorEditsShiftFoldsAndMatchingWithoutRebuildingAndUndoRestoresThem(string syntax)
    {
        var vm = new QueryTabViewModel { QueryText = "(\nCustomerName\n)\n(\nOtherName\n)" };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            var behavior = typeof(QueryTabView).GetField("_editorStructure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var structureField = behavior.GetType().GetField("_structure", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = structureField.GetValue(behavior);
            var manager = (FoldingManager)behavior.GetType().GetField("_foldings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
            var second = manager.AllFoldings.Last();
            second.IsFolded = true;
            var sql = editor.Text;
            editor.TextArea.Focus();
            editor.CaretOffset = 6;
            window.KeyTextInput("xyz");
            Check();
            editor.Undo();
            Assert.Equal(sql, editor.Text);
            Check();
            editor.Redo();
            Check();
            editor.Undo();
            editor.Document.Insert(6, syntax);
            Assert.NotSame(original, structureField.GetValue(behavior));
            Check(false);
            editor.Undo();
            Check(false);
            editor.Document = new TextDocument("(\nAnotherName\n)");
            Check(false);
            window.Content = null;
            editor.Document.Insert(5, "x");
            window.Content = view;
            Check(false);

            void Check(bool incremental = true)
            {
                manager = (FoldingManager)behavior.GetType().GetField("_foldings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
                var actual = (SqlEditorStructure)structureField.GetValue(behavior)!;
                if (incremental) Assert.Same(original, actual);
                var expected = SqlEditorStructure.Analyze(editor.Text);
                Assert.Equal(editor.Text, vm.QueryText);
                Assert.Equal(expected.Foldings, actual.Foldings);
                Assert.Equal(expected.Foldings.Select(s => (s.Start, s.End)), manager.AllFoldings.Select(f => (f.StartOffset, f.EndOffset)));
                for (var caret = 0; caret <= editor.Text.Length; caret++)
                    Assert.Equal(expected.FindMatchingParentheses(caret), actual.FindMatchingParentheses(caret));
                if (incremental) Assert.True(second.IsFolded);
            }
        }
        finally { window.Close(); view.DataContext = null; }
    }
}
