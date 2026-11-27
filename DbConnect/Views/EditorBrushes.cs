using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace DbConnect.Views;

/// <summary>Brushes the editor's background renderers draw with, resolved for the editor's theme variant.</summary>
internal static class EditorBrushes
{
    public const string ErrorUnderline = "DbConnectEditorErrorUnderlineBrush";
    public const string RunningStatement = "DbConnectEditorRunningStatementBrush";
    public const string BracketMatch = "DbConnectEditorBracketMatchBrush";
    public const string BracketMatchBorder = "DbConnectEditorBracketMatchBorderBrush";

    public static IBrush Resolve(TextView textView, string key) =>
        textView.TryFindResource(key, textView.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}
