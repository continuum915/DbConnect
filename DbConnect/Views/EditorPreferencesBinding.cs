using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using DbConnect.Services;

namespace DbConnect.Views;

/// <summary>Keeps a SQL editor's font, indentation and wrapping in line with the editor preferences.</summary>
internal static class EditorPreferencesBinding
{
    public static void Attach(TextEditor editor)
    {
        void Apply()
        {
            var preferences = AppPreferences.Editor;
            editor.FontFamily = new FontFamily(preferences.FontFamily);
            editor.FontSize = preferences.FontSize;
            editor.Options.IndentationSize = preferences.TabWidth;
            editor.Options.ConvertTabsToSpaces = preferences.InsertSpaces;
            editor.Options.HighlightCurrentLine = true;
            editor.WordWrap = LargeDocumentMode.WordWrapFor(editor);
        }

        void OnChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        // Subscribe only while shown, so closed tabs do not stay reachable from the static event.
        editor.AttachedToVisualTree += (_, _) => { AppPreferences.Changed += OnChanged; Apply(); };
        editor.DetachedFromVisualTree += (_, _) => AppPreferences.Changed -= OnChanged;
        Apply();
    }
}
