using System;
using System.Collections.Generic;

namespace DbConnect.Services;

/// <summary>Foldable SQL regions and paired parentheses outside quoted text and comments.</summary>
public sealed class SqlEditorStructure
{
    private readonly record struct Parenthesis(int Offset, int MatchingOffset);

    // Appended in document order, so caret lookup needs no hash table or extra sorting.
    private readonly List<Parenthesis> _pairs = [];
    private readonly List<StatementSpan> _foldings = [];
    private int _textLength;
    internal int TextLength => _textLength;

    private SqlEditorStructure(int textLength) => _textLength = textLength;

    /// <summary>Multiline regions in document order; End is exclusive.</summary>
    public IReadOnlyList<StatementSpan> Foldings => _foldings;

    public static SqlEditorStructure Analyze(string text, SqlBatchDialect dialect = SqlBatchDialect.Standard)
    {
        var structure = new SqlEditorStructure(text.Length);
        var openings = new Stack<int>();
        foreach (var token in SqlTokenizer.EnumerateRanges(text, dialect))
        {
            if (token.Kind == SqlTokenKind.Comment && token.IsTerminated &&
                text.AsSpan(token.Start, token.Length).StartsWith("/*", StringComparison.Ordinal))
            {
                structure.AddFolding(text, token.Start, token.Start + token.Length);
            }
            else if (token.Kind == SqlTokenKind.Symbol && token.Length == 1)
            {
                if (text[token.Start] == '(')
                {
                    openings.Push(structure._pairs.Count);
                    structure._pairs.Add(new Parenthesis(token.Start, -1));
                }
                else if (text[token.Start] == ')' && openings.TryPop(out var openingIndex))
                {
                    var opening = structure._pairs[openingIndex].Offset;
                    structure._pairs[openingIndex] = new Parenthesis(opening, token.Start);
                    structure._pairs.Add(new Parenthesis(token.Start, opening));
                    structure.AddFolding(text, opening, token.Start + 1);
                }
            }
        }

        structure._foldings.Sort((left, right) => left.Start.CompareTo(right.Start));
        return structure;
    }

    /// <summary>Matches immediately before or at the caret, preferring the preceding parenthesis.</summary>
    public (int Opening, int Closing)? FindMatchingParentheses(int caretOffset)
    {
        if (caretOffset < 0 || caretOffset > _textLength)
            return null;
        return FindPair(caretOffset - 1) ?? FindPair(caretOffset);
    }

    private (int Opening, int Closing)? FindPair(int offset)
    {
        var left = 0;
        var right = _pairs.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            var pair = _pairs[middle];
            if (pair.Offset < offset) left = middle + 1;
            else if (pair.Offset > offset) right = middle - 1;
            else if (pair.MatchingOffset < 0) return null;
            else return pair.Offset < pair.MatchingOffset
                ? (pair.Offset, pair.MatchingOffset) : (pair.MatchingOffset, pair.Offset);
        }
        return null;
    }

    internal int FindEditContextStart(int offset)
    {
        // A real parenthesis token is a neutral lexer boundary, including for E-strings and GO lines.
        var left = 0;
        var right = _pairs.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            if (_pairs[middle].Offset <= offset) left = middle + 1;
            else right = middle - 1;
        }
        return right >= 0 ? _pairs[right].Offset : 0;
    }

    internal void ShiftNonStructuralEdit(int offset, int removed, int inserted)
    {
        var delta = inserted - removed;
        _textLength += delta;
        if (delta == 0) return;
        for (var i = 0; i < _pairs.Count; i++)
        {
            var pair = _pairs[i];
            _pairs[i] = new Parenthesis(pair.Offset >= offset ? pair.Offset + delta : pair.Offset,
                pair.MatchingOffset >= offset ? pair.MatchingOffset + delta : pair.MatchingOffset);
        }
        for (var i = 0; i < _foldings.Count; i++)
        {
            var span = _foldings[i];
            var start = span.Start >= offset ? span.Start + delta : span.Start;
            var end = span.End >= offset ? span.End + delta : span.End;
            _foldings[i] = new StatementSpan(start, end - start);
        }
    }

    private void AddFolding(string text, int start, int end)
    {
        if (text.AsSpan(start, end - start).IndexOfAny('\r', '\n') >= 0)
            _foldings.Add(new StatementSpan(start, end - start));
    }
}
