using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Very large query text loads without syntax highlighting or word wrap, which made it slow or hang.</summary>
[Collection("AppPreferences")]
public sealed class LargeDocumentModeTests : IDisposable
{
    public LargeDocumentModeTests() =>
        AppPreferences.Apply(new AppSettings { Editor = new EditorPreferences { WordWrap = true } });

    public void Dispose() => AppPreferences.Apply(new AppSettings());

    [AvaloniaFact]
    public void LargeText_TurnsHighlightingAndWrapOff_AndSmallTextTurnsThemBackOn()
    {
        var editor = new TextEditor { Text = "SELECT 1" };
        using var host = ThemedHost.Show(editor, "Light");
        var mode = LargeDocumentMode.Attach(editor, ".sql");
        EditorPreferencesBinding.Attach(editor);
        Assert.True(mode.IsHighlighting);
        Assert.True(editor.WordWrap);

        editor.Text = "SELECT '" + new string('x', LargeDocumentMode.MaxCharacters) + "';";

        Assert.False(mode.IsHighlighting);
        Assert.False(editor.WordWrap);
        Assert.Equal(LargeDocumentMode.MaxCharacters + 10, editor.Document.TextLength);

        editor.Text = "SELECT 2";

        Assert.True(mode.IsHighlighting);
        Assert.True(editor.WordWrap);
    }

    [Theory]
    [InlineData(999_999, 1_000, false)]
    [InlineData(1_000_000, 50_000, true)]
    [InlineData(100_000, 50, true)]
    [InlineData(100_000, 51, false)]
    [InlineData(99_999, 1, false)]
    public void IsLarge_CountsSizeAndAverageLineLength(int characters, int lines, bool large)
    {
        var text = string.Join("\n", Enumerable.Repeat(new string('x', (characters - (lines - 1)) / lines), lines));
        text += new string('x', characters - text.Length);

        Assert.Equal(large, LargeDocumentMode.IsLarge(new AvaloniaEdit.Document.TextDocument(text)));
    }
}
