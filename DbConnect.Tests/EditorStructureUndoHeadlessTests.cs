using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using DbConnect.PerformanceTests;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class EditorStructureUndoHeadlessTests
{
    [AvaloniaFact]
    public void PastedScriptKeepsFoldStateAndMatchingAcrossEditUndoRedo()
    {
        var sql = RepresentativeImport.Generate(1024 * 1024).Sql;
        var vm = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            editor.Clear();
            editor.Document.UndoStack.ClearAll();
            editor.Document.Insert(0, sql);
            var behavior = typeof(QueryTabView).GetField("_editorStructure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var manager = (FoldingManager)behavior.GetType().GetField("_foldings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
            var first = manager.AllFoldings.First();
            first.IsFolded = true;
            var count = manager.AllFoldings.Count();
            const string suffix = "\n(\n42\n)\n";
            using (editor.Document.RunUpdate()) editor.Document.Insert(sql.Length, suffix);
            AssertDocument(sql + suffix);
            Assert.True(first.IsFolded);
            Assert.Equal(count + 1, manager.AllFoldings.Count());
            var last = manager.AllFoldings.Last();
            Assert.Equal(sql.Length + 1, last.StartOffset);
            Assert.Equal(sql.Length + 7, last.EndOffset);
            AssertMatching(sql.Length + 1, sql.Length + 6);
            const string prefix = "-- prefix\n";
            using (editor.Document.RunUpdate()) editor.Document.Insert(0, prefix);
            AssertDocument(prefix + sql + suffix);
            Assert.True(first.IsFolded);
            AssertMatching(prefix.Length + sql.Length + 1, prefix.Length + sql.Length + 6);
            editor.Undo();
            AssertDocument(sql + suffix);
            Assert.True(first.IsFolded);
            AssertMatching(sql.Length + 1, sql.Length + 6);

            editor.Undo();
            AssertDocument(sql);
            Assert.True(first.IsFolded);
            Assert.Equal(count, manager.AllFoldings.Count());
            editor.Redo();
            AssertDocument(sql + suffix);
            AssertMatching(sql.Length + 1, sql.Length + 6);
            editor.Undo();
            editor.Undo();
            AssertDocument("");
            Assert.Empty(manager.AllFoldings);
            editor.Redo();
            AssertDocument(sql);
            Assert.Equal(count, manager.AllFoldings.Count());

            void AssertDocument(string expected)
            {
                Assert.Equal(expected, editor.Text);
                Assert.Equal(expected, vm.QueryText);
            }

            void AssertMatching(int opening, int closing)
            {
                editor.CaretOffset = opening;
                var renderer = editor.TextArea.TextView.BackgroundRenderers.Single(r => r.GetType().Name == "MatchingParenthesisRenderer");
                Assert.Equal<object>((opening, closing), renderer.GetType().GetProperty("Pair")!.GetValue(renderer));
            }
        }
        finally { window.Close(); view.DataContext = null; }
    }
}
