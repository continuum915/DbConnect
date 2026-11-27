using Avalonia.Controls;
using AvaloniaEdit;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Shows "Ln, Col · encoding · line endings" in the window's status bar while a SQL editor has focus, and clears it
/// when the editor loses focus.
/// </summary>
internal static class EditorCaretStatus
{
    public static void Attach(TextEditor editor)
    {
        editor.TextArea.Caret.PositionChanged += (_, _) => Show(editor);
        editor.TextArea.GotFocus += (_, _) => Show(editor);
        editor.TextArea.LostFocus += (_, _) => StatusBar(editor)?.SetCaretText(null);
    }

    private static void Show(TextEditor editor)
    {
        if (StatusBar(editor) is not { } status || !editor.TextArea.IsFocused) return;
        var caret = editor.TextArea.Caret;
        var encoding = (editor.DataContext as QueryTabViewModel)?.Document?.Encoding.WebName.ToUpperInvariant() ?? "UTF-8";
        // The first line's delimiter names the file's line endings, without reading the whole text.
        var first = editor.Document?.LineCount > 0 ? editor.Document.GetLineByNumber(1) : null;
        var endings = first?.DelimiterLength switch
        {
            2 => "CRLF",
            1 => "LF",
            _ => System.OperatingSystem.IsWindows() ? "CRLF" : "LF",
        };
        status.SetCaretText($"Ln {caret.Line}, Col {caret.Column} · {encoding} · {endings}");
    }

    private static StatusBarState? StatusBar(Control editor) =>
        (TopLevel.GetTopLevel(editor)?.DataContext as MainWindowViewModel)?.Frame.StatusBar;
}
