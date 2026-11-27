using System;

namespace DbConnect.Services;

/// <summary>Updates structure for proven token-interior edits; other changes use the full analyzer.</summary>
public sealed class SqlEditorStructureTracker
{
    private const int ContextLimit = 8192;
    private SqlBatchDialect _dialect;
    private int _changes;
    private (int Offset, int Removed, int Inserted)? _pending;

    public SqlEditorStructureTracker(string text, SqlBatchDialect dialect = SqlBatchDialect.Standard)
    {
        _dialect = dialect;
        Structure = SqlEditorStructure.Analyze(text, dialect);
    }

    /// <summary>The current structure; eligible edits update it in place.</summary>
    public SqlEditorStructure Structure { get; private set; }

    /// <summary>Called before each document change, reading only a bounded old-text context.</summary>
    public bool PrepareEdit(int offset, int removalLength, string insertion, Func<int, int, string> readText)
    {
        _pending = null;
        if (++_changes != 1 || _dialect == SqlBatchDialect.MySql ||
            offset < 0 || removalLength < 0 || offset > Structure.TextLength - removalLength ||
            removalLength > 256 || insertion.Length > 256 || !IsWordText(insertion.AsSpan()))
            return false;
        var start = Structure.FindEditContextStart(offset);
        var length = Math.Min(ContextLimit, Structure.TextLength - start);
        if (offset - start + removalLength >= length) return false;
        var context = readText(start, length);
        var relative = offset - start;
        if (!IsWordText(context.AsSpan(relative, removalLength))) return false;
        foreach (var token in SqlTokenizer.EnumerateRanges(context, _dialect))
        {
            if (token.Start > relative) break;
            if (token.Kind is SqlTokenKind.Word or SqlTokenKind.Number &&
                relative > token.Start && relative + removalLength < token.Start + token.Length)
            {
                _pending = (offset, removalLength, insertion.Length);
                return true;
            }
        }
        return false;
    }

    /// <summary>Marks a change that must use the full analyzer without materializing its inserted text.</summary>
    public void InvalidateEdit()
    {
        _pending = null;
        _changes = 2;
    }

    /// <summary>Called once after the document update. Returns true when the full scan/fold refresh is unnecessary.</summary>
    public bool CompleteEdit(Func<string> readText, SqlBatchDialect dialect)
    {
        var pending = _pending;
        _pending = null;
        var eligible = _changes == 1 && dialect == _dialect;
        _changes = 0;
        _dialect = dialect;
        if (eligible && pending is { } edit)
        {
            Structure.ShiftNonStructuralEdit(edit.Offset, edit.Removed, edit.Inserted);
            return true;
        }
        Structure = SqlEditorStructure.Analyze(readText(), dialect);
        return false;
    }

    private static bool IsWordText(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
        return true;
    }
}
