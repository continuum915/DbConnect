using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class EditorSelectionHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryEditor_SelectedTextHasAVisibleBackground(bool dark)
    {
        var view = new QueryTabView { DataContext = new QueryTabViewModel { QueryText = "select 42;" } };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        try
        {
            window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Show();
            window.UpdateLayout();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            editor.TextArea.Focus();
            editor.Select(0, 6);
            Assert.Equal("select", editor.SelectedText);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.SelectionBrush);
            Assert.True(brush.Color.A > 0 && brush.Opacity > 0, $"Selection brush is {brush.Color}, opacity {brush.Opacity}.");
            Assert.NotEqual(brush.Color, Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.SelectionForeground).Color);
            window.FocusManager!.ClearFocus();
            Assert.False(editor.TextArea.IsFocused);
            Assert.Equal("select", editor.SelectedText);
            Assert.True(Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.SelectionBrush).Color.A > 0);
        }
        finally { window.Close(); }
    }
}
