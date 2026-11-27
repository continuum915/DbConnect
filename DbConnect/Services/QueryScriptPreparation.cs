using System.Threading;

namespace DbConnect.Services;

internal sealed record QueryRunSource(string Sql, int Offset, bool IsEmpty);

internal static class QueryScriptPreparation
{
    internal static QueryRunSource Resolve(string editorSql, string selection, bool selectionOnly,
        int selectionOffset, int caretOffset, SqlBatchDialect dialect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sql = selectionOnly ? selection : editorSql;
        var offset = selectionOnly ? selectionOffset : 0;
        if (selectionOnly && sql.Length == 0)
        {
            var span = SqlBatchParser.FindStatementAt(editorSql, caretOffset, dialect, token);
            if (span is { } statement)
            {
                sql = editorSql.Substring(statement.Start, statement.Length);
                offset = statement.Start;
            }
        }
        return new(sql, offset, sql.Length == 0 || !selectionOnly && SqlScanCancellation.IsWhiteSpace(sql, token));
    }

    internal static StatementSpan[] HighlightSpans(string sql, int sourceOffset,
        SqlBatchDialect dialect, CancellationToken token)
    {
        var spans = SqlBatchParser.GetStatementSpans(sql, dialect, token);
        var result = new StatementSpan[spans.Count];
        for (var i = 0; i < result.Length; i++)
        {
            SqlScanCancellation.Check(token, i);
            result[i] = new(sourceOffset + spans[i].Start, spans[i].Length);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
