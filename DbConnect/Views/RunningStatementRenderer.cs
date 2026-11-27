using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DbConnect.Services;

namespace DbConnect.Views;

internal sealed class RunningStatementRenderer : IBackgroundRenderer
{
    public StatementSpan? Span { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Span is not { Length: > 0 } span || span.Start < 0 || span.End > textView.Document.TextLength)
            return;

        var highlight = EditorBrushes.Resolve(textView, EditorBrushes.RunningStatement);
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(span.Start, span.Length)))
            drawingContext.DrawRectangle(highlight, null, rect);
    }
}
