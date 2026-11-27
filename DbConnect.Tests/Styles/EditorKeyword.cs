using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace DbConnect.Tests.Styles;

/// <summary>Reads the colour an editor draws the keyword at the start of its first line in.</summary>
internal static class EditorKeyword
{
    /// <summary>The foreground the editor draws "SELECT" in, once the background tokenizer has coloured it.</summary>
    public static Color Colour(TextEditor editor, Color? notEqualTo = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var colour = ColourOrNull(editor);
            if (colour is { } found && found != notEqualTo)
                return found;
            Assert.True(DateTime.UtcNow < deadline, $"SELECT was not coloured (last: {colour?.ToString() ?? "none"}).");
            Thread.Sleep(20);
        }
    }

    public static Color? ColourOrNull(TextEditor editor)
    {
        Dispatcher.UIThread.RunJobs();
        var textView = editor.TextArea.TextView;
        textView.Redraw();
        editor.UpdateLayout();
        var line = textView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        var brush = line.Elements.OfType<VisualLineText>().FirstOrDefault()?.TextRunProperties.ForegroundBrush;
        return brush is ISolidColorBrush solid && solid.Color != (editor.Foreground as ISolidColorBrush)?.Color ? solid.Color : null;
    }
}
