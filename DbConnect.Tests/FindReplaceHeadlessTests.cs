using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class FindReplaceHeadlessTests
{
    [AvaloniaFact]
    public void QueryEditor_FindReplaceUpdatesQueryTextWithinTheCapturedSelection()
    {
        var vm = new QueryTabViewModel { QueryText = "cat cat cat" };
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var bar = view.FindControl<FindReplaceBar>("EditorSearch")!;
        editor.Select(4, 3);

        view.OpenSearch();
        var session = bar.Session!;
        session.SearchText = "cat";
        session.ReplacementText = "longer";
        session.InSelection = true;
        session.FindNextCommand.Execute(null);
        Assert.Equal(4, editor.SelectionStart);
        Assert.Equal("cat", editor.SelectedText);
        session.ReplaceAllCommand.Execute(null);

        Assert.Equal("cat longer cat", vm.QueryText);
        editor.Document.UndoStack.Undo();
        Assert.Equal("cat cat cat", vm.QueryText);
        editor.IsReadOnly = true;
        Assert.False(session.ReplaceAllCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ReopeningTheBar_CapturesTheNewSelection()
    {
        var editor = new TextEditor { Text = "cat cat cat" };
        var bar = new FindReplaceBar();
        bar.Attach(editor);
        editor.Select(0, 3);
        bar.Open();
        bar.Session!.CloseCommand.Execute(null);
        editor.Select(8, 3);
        bar.Open();
        bar.Session.SearchText = "cat";
        bar.Session.ReplacementText = "dog";
        bar.Session.InSelection = true;

        bar.Session.ReplaceAllCommand.Execute(null);

        Assert.Equal("cat cat dog", editor.Text);
    }
    [AvaloniaFact]
    public void KeyboardShortcuts_OpenFindReplaceAndCloseBackToEditor()
    {
        var view = new QueryTabView { DataContext = new QueryTabViewModel { QueryText = "SELECT item FROM orders;" } };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var bar = view.FindControl<FindReplaceBar>("EditorSearch")!;
        var window = new Window { Content = view, Width = 1000, Height = 650 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.Focus();
            Assert.True(editor.TextArea.IsFocused);
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.Session!.IsOpen);
            Assert.True(bar.FindControl<TextBox>("FindBox")!.IsFocused);
            bar.FindControl<TextBox>("FindBox")!.Text = "item";
            bar.FindControl<TextBox>("ReplaceBox")!.Text = "product";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("item", bar.Session.SearchText);
            Assert.Equal("product", bar.Session.ReplacementText);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("item", editor.SelectedText);
            Assert.True(bar.Bounds.Height > 0);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(bar.Session.IsOpen);
            Assert.True(editor.TextArea.IsFocused);
            window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control);
            Assert.True(bar.FindControl<TextBox>("ReplaceBox")!.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }
}
