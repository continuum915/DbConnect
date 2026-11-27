using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace DbConnect.Views;

internal sealed class MatchingParenthesisRenderer : IBackgroundRenderer
{
    public (int Opening, int Closing)? Pair { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Pair is not { } pair || pair.Opening < 0 || pair.Closing >= textView.Document.TextLength)
            return;

        var highlight = EditorBrushes.Resolve(textView, EditorBrushes.BracketMatch);
        var outline = new Pen(EditorBrushes.Resolve(textView, EditorBrushes.BracketMatchBorder));
        DrawParenthesis(pair.Opening, textView, drawingContext, highlight, outline);
        DrawParenthesis(pair.Closing, textView, drawingContext, highlight, outline);
    }

    private static void DrawParenthesis(int offset, TextView textView, DrawingContext drawingContext, IBrush highlight, Pen outline)
    {
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(offset, 1)))
            drawingContext.DrawRectangle(highlight, outline, rect);
    }
}
