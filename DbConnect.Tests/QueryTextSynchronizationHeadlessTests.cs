using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class QueryTextSynchronizationHeadlessTests
{
    [AvaloniaFact]
    public void UnreadUndoRestoresRemainAvailableForSubsequentInverseEdits()
    {
        var original = new string("SELECT CustomerName;".ToCharArray());
        var vm = new QueryTabViewModel { QueryText = original };
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        try
        {
            editor.Document.Insert(10, "x");
            Assert.Contains("x", vm.QueryText);
            editor.Undo(); // Leave the restored revision unread, like the audit's cleanup operations.
            editor.Document.Replace(10, 1, "q");
            Assert.Contains("q", vm.QueryText);
            editor.Undo();
            editor.Document.Remove(10, 1);
            Assert.Equal("SELECT CusomerName;", vm.QueryText);
            editor.Undo();
            Assert.Same(original, vm.QueryText);
        }
        finally { view.DataContext = null; }
    }
    [AvaloniaFact]
    public void ImmediateUndoAndRedoReuseLiveMaterializedStrings()
    {
        var original = new string("SELECT CustomerName;".ToCharArray());
        var vm = new QueryTabViewModel { QueryText = original };
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        editor.Document.Insert(10, "z");
        var typed = vm.QueryText;
        editor.Undo();
        Assert.Same(original, vm.QueryText);
        editor.Redo();
        Assert.Same(typed, vm.QueryText);
        view.DataContext = null;
    }

    [AvaloniaFact]
    public async Task CurrentTextSurvivesUndoRedoDocumentReplacementDetachAndBackgroundReads()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT CustomerName;" };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        try
        {
            window.Show();
            editor.Document.Insert(10, "z");
            Assert.Equal(editor.Text, vm.QueryText);
            editor.Undo();
            Assert.Equal("SELECT CustomerName;", vm.QueryText);
            editor.Redo();
            Assert.Equal(editor.Text, vm.QueryText);
            editor.Document = new TextDocument("SELECT NewRevision;");
            window.UpdateLayout();
            Assert.Equal("SELECT NewRevision;", vm.QueryText);
            editor.Document = null!;
            Assert.Equal(string.Empty, vm.QueryText);
            editor.Document = new TextDocument("SELECT NewRevision;");
            view.DataContext = null;
            editor.Text = "SELECT Detached;";
            Assert.Equal("SELECT NewRevision;", await Task.Run(() => vm.QueryText));
            view.DataContext = vm;
            Assert.Equal("SELECT NewRevision;", editor.Text);
            editor.Document.Insert(10, "x");
            var expected = editor.Text;
            view.DataContext = null;
            window.Close();
            Assert.Equal(expected, await Task.Run(() => vm.QueryText));
        }
        finally { window.Close(); view.DataContext = null; }
    }

    [AvaloniaFact]
    public void NoopReplacementsAndBatchedNetUnchangedEditsDoNotDirtyTheTab()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT CustomerName;" };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            vm.ClearDirty();
            editor.Document.Replace(0, 6, "SELECT");
            Assert.False(vm.IsDirty);
            using (editor.Document.RunUpdate()) { editor.Document.Insert(10, "x"); editor.Document.Remove(10, 1); }
            Assert.False(vm.IsDirty);
            editor.Document.Insert(10, "x");
            Assert.True(vm.IsDirty);
            Assert.Equal(editor.Text, vm.QueryText);
        }
        finally { window.Close(); view.DataContext = null; }
    }

    [AvaloniaFact]
    public void ReentrantModelAssignmentsWinOverAnEditorPublication()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT CustomerName;" };
        var replaced = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.QueryText) && !replaced)
            { replaced = true; vm.QueryText = "SELECT Authoritative;"; }
        };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            editor.Document.Insert(10, "z");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("SELECT Authoritative;", editor.Text);
            Assert.Equal(editor.Text, vm.QueryText);
        }
        finally { window.Close(); view.DataContext = null; }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WatermarkVisibilityUsesLengthForEmptyAndNonemptyRevisions(bool dark)
    {
        var view = new QueryTabView { DataContext = new QueryTabViewModel { QueryText = "" } };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        editor.TextArea.Watermark = "Enter SQL";
        var window = new Window { Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); window.UpdateLayout();
            var watermark = editor.TextArea.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Watermark");
            Assert.True(watermark.IsVisible);
            Assert.Equal("Enter SQL", watermark.Text);
            editor.Document.Insert(0, " ");
            Assert.False(watermark.IsVisible);
            editor.Undo();
            Assert.True(watermark.IsVisible);
            editor.Document = new TextDocument("SELECT 1");
            window.UpdateLayout();
            Assert.False(watermark.IsVisible);
            editor.Clear();
            Assert.True(watermark.IsVisible);
        }
        finally { window.Close(); view.DataContext = null; }
    }

    [AvaloniaFact]
    public async Task SavedQueriesAndCompletionConsumersReadTheLatestRevision()
    {
        var path = Path.Combine(Path.GetTempPath(), "rain-lazy-query-" + Guid.NewGuid().ToString("N") + ".json");
        var service = new QueryWorkspaceService(path);
        var vm = new QueryTabViewModel(workspaceService: service) { QueryText = "SELECT CustomerName;", SaveQueryName = "Latest" };
        var view = new QueryTabView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            editor.Document.Insert(10, "z");
            var expected = editor.Text;
            await vm.SaveCurrentQueryCommand.ExecuteAsync(null);
            Assert.Equal(expected, Assert.Single(await service.LoadSavedQueriesAsync()).Sql);
            editor.Text = "SELECT 'unclosed";
            Assert.True(SqlCompletionGuard.IsInsideStringOrComment(vm.QueryText, editor.CaretOffset = editor.Document.TextLength));
            editor.Clear();
            editor.Document.Insert(0, "SEL");
            Assert.Contains(SqlCompletionEngine.GetCandidates(vm.QueryText, 3, new DbConnect.Models.SchemaIndex()), c => c.Text == "SELECT");
        }
        finally { window.Close(); view.DataContext = null; File.Delete(path); }
    }
}
