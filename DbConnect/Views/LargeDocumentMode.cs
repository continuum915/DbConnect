using System;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using DbConnect.Services;

namespace DbConnect.Views;

/// <summary>
/// Keeps a SQL editor usable when it holds a very large document or very long lines: syntax highlighting
/// and word wrap are switched off while the document is large and back on when it is not. The text itself
/// is never changed. A 5 MB query on three lines took 2.3 s to lay out highlighted and 0.85 s without; with
/// word wrap it did not finish in ten minutes.
/// </summary>
internal sealed class LargeDocumentMode
{
    internal const int MaxCharacters = 1_000_000;
    internal const int LongLineDocumentCharacters = 100_000;
    internal const int MaxAverageLineCharacters = 2_000;

    private readonly TextEditor _editor;
    private readonly string _fileExtension;
    private EditorThemeBinding? _highlighting;
    private bool _highlightingFailed;

    private LargeDocumentMode(TextEditor editor, string fileExtension)
    {
        _editor = editor;
        _fileExtension = fileExtension;
        editor.TextChanged += (_, _) => Update();
        editor.DocumentChanged += (_, _) => Update();
        Update();
    }

    /// <summary>Highlights <paramref name="editor"/> as <paramref name="fileExtension"/> unless its document is large.</summary>
    public static LargeDocumentMode Attach(TextEditor editor, string fileExtension) => new(editor, fileExtension);

    public bool IsHighlighting => _highlighting != null;

    /// <summary>True for a document too large, or with lines too long, to highlight or wrap quickly.</summary>
    public static bool IsLarge(TextDocument? document) =>
        document is { } d && (d.TextLength >= MaxCharacters ||
            (d.TextLength >= LongLineDocumentCharacters && d.TextLength / Math.Max(1, d.LineCount) >= MaxAverageLineCharacters));

    /// <summary>The word wrap the editor should use: the preference, except never on a large document.</summary>
    public static bool WordWrapFor(TextEditor editor) => AppPreferences.Editor.WordWrap && !IsLarge(editor.Document);

    private void Update()
    {
        var large = IsLarge(_editor.Document);
        // Switch wrapping off before the next layout; the text-changed event runs before it.
        var wrap = WordWrapFor(_editor);
        if (_editor.WordWrap != wrap)
            _editor.WordWrap = wrap;

        if (large && _highlighting != null)
        {
            _highlighting.Dispose();
            _highlighting = null;
        }
        else if (!large && _highlighting == null && !_highlightingFailed)
        {
            _highlighting = EditorThemeBinding.TryAttach(_editor, _fileExtension);
            _highlightingFailed = _highlighting == null;
        }
    }
}
