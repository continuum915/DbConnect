using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using DbConnect.Models;

namespace DbConnect.Tests;

public sealed class SqlCompletionPopupThemeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionPopupLoadsWithTheApplicationTheme(bool dark)
    {
        var editor = new TextEditor { Text = "SELECT public.rain_metadata_smoke." };
        var window = new Window
        {
            Content = editor,
            Width = 640,
            Height = 400,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        CompletionWindow? completion = null;
        try
        {
            window.Show();
            window.UpdateLayout();
            editor.CaretOffset = editor.Text.Length;
            completion = new CompletionWindow(editor.TextArea);
            completion.CompletionList.CompletionData.Add(new SqlCompletionData("original", CompletionKind.Column));
            completion.Show();
            completion.UpdateLayout();
            Assert.True(completion.IsVisible);
            Assert.Equal(window.ActualThemeVariant, completion.ActualThemeVariant);
            Assert.Equal("original", Assert.Single(completion.CompletionList.CompletionData).Text);
        }
        finally
        {
            completion?.Close();
            window.Close();
        }
    }
}
