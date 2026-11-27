using DbConnect.Services;

namespace DbConnect.Views;

public partial class QueryTabView
{
    private void UpdateErrorSpan(StatementSpan? span)
    {
        _errorUnderlineRenderer.Span = span;
        QueryEditor?.TextArea.TextView.InvalidateLayer(_errorUnderlineRenderer.Layer);
        if (span is { } location && QueryEditor != null && location.Start < QueryEditor.Document.TextLength)
        {
            QueryEditor.CaretOffset = location.Start;
            QueryEditor.ScrollToLine(QueryEditor.Document.GetLineByOffset(location.Start).LineNumber);
            QueryEditor.Focus();
        }
    }
}
