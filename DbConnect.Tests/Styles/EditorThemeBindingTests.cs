using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 03 (K4), UI redesign 02: SQL syntax colours are the palette's SQL roles for the editor's theme
/// variant and switch live.</summary>
public sealed class EditorThemeBindingTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    /// <summary>The palette's SQL keyword colour in <paramref name="variant"/>.</summary>
    private static Color Keyword(Control host, ThemeVariant variant) =>
        host.TryFindResource("DbConnect.Color.SqlKeyword", variant, out var colour) ? (Color)colour! : default;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void Keywords_UseTheVariantsSyntaxThemeAndAreReadableOnTheEditor(string variant)
    {
        var editor = SqlEditor();
        using var host = ThemedHost.Show(editor, variant);
        using var binding = EditorThemeBinding.Attach(editor, ".sql");

        var keyword = EditorKeyword.Colour(editor);
        Assert.Equal(Keyword(editor, variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light), keyword);
        Contrast.AssertReadable(new SolidColorBrush(keyword), editor.Background, "editor keyword");
    }

    [AvaloniaFact]
    public void SwitchingTheme_RecoloursAnOpenEditor()
    {
        var editor = SqlEditor();
        using var host = ThemedHost.Show(editor, "Light");
        using var binding = EditorThemeBinding.Attach(editor, ".sql");
        var light = Keyword(editor, ThemeVariant.Light);
        var dark = Keyword(editor, ThemeVariant.Dark);
        Assert.Equal(light, EditorKeyword.Colour(editor));

        host.Window.RequestedThemeVariant = ThemeVariant.Dark;

        Assert.Equal(dark, EditorKeyword.Colour(editor, notEqualTo: light));
    }

    [AvaloniaFact]
    public void Dispose_RemovesTheHighlighting()
    {
        var editor = SqlEditor();
        using var host = ThemedHost.Show(editor, "Light");
        EditorThemeBinding.Attach(editor, ".sql").Dispose();

        host.Window.RequestedThemeVariant = ThemeVariant.Dark;

        Assert.Null(EditorKeyword.ColourOrNull(editor));
    }

    /// <summary>A SQL editor on the code background, as the views declare it.</summary>
    private static TextEditor SqlEditor()
    {
        var editor = new TextEditor { Text = "SELECT 1" };
        editor.Bind(TextEditor.BackgroundProperty, editor.GetResourceObservable("DbConnectCodeBgBrush"));
        return editor;
    }
}
