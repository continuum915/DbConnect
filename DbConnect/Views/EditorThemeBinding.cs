using System;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace DbConnect.Views;

/// <summary>
/// Installs TextMate highlighting on an editor with the syntax theme of its theme variant (<see cref="EditorSyntaxTheme"/>,
/// from the palette's SQL roles) and switches theme when the variant changes. Dispose removes the highlighting.
/// </summary>
internal sealed class EditorThemeBinding : IDisposable
{
    private readonly TextEditor _editor;
    private readonly TextMate.Installation _installation;
    private ThemeVariant _variant;

    private EditorThemeBinding(TextEditor editor, string fileExtension)
    {
        _editor = editor;
        _variant = editor.ActualThemeVariant;
        // The registry supplies the grammars; its bundled theme is replaced by the palette theme straight away.
        var registryOptions = new RegistryOptions(ThemeName.LightPlus);
        _installation = editor.InstallTextMate(registryOptions);
        _installation.SetGrammar(registryOptions.GetScopeByLanguageId(registryOptions.GetLanguageByExtension(fileExtension).Id));
        _installation.SetTheme(EditorSyntaxTheme.For(editor, _variant));
        editor.ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    /// <summary>Highlights the editor as <paramref name="fileExtension"/> (".sql", ".json"); null if TextMate cannot load.</summary>
    public static EditorThemeBinding? TryAttach(TextEditor editor, string fileExtension)
    {
        try
        {
            return Attach(editor, fileExtension);
        }
        catch
        {
            // Highlighting is cosmetic; the text is still shown without it.
            return null;
        }
    }

    public static EditorThemeBinding Attach(TextEditor editor, string fileExtension) => new(editor, fileExtension);

    public void Dispose()
    {
        _editor.ActualThemeVariantChanged -= OnThemeVariantChanged;
        _installation.Dispose();
        _editor.TextArea.TextView.Redraw();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        var variant = _editor.ActualThemeVariant;
        if (variant == _variant)
            return;

        _variant = variant;
        _installation.SetTheme(EditorSyntaxTheme.For(_editor, variant));
        _editor.TextArea.TextView.Redraw();
    }
}
