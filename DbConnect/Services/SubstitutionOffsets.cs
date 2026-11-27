using System;
using System.Collections;
using System.Collections.Generic;

namespace DbConnect.Services;

/// <summary>A placeholder's replacement, located in both the source script and the substituted result.</summary>
internal readonly record struct SqlReplacement(int ResultStart, int SourceStart, int SourceLength, string Literal);

/// <summary>
/// Maps each character of a substituted script to its source offset. Stored per replacement rather than per
/// character, because a large script rarely needs this: only navigating to an error reads it.
/// </summary>
internal sealed class SubstitutionOffsets : IReadOnlyList<int>
{
    private readonly IReadOnlyList<SqlReplacement> _replacements;
    private readonly int _sourceLength;

    public SubstitutionOffsets(IReadOnlyList<SqlReplacement> replacements, int sourceLength, int resultLength)
    {
        _replacements = replacements;
        _sourceLength = sourceLength;
        Count = resultLength;
    }

    public int Count { get; }

    public int this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            // The last replacement starting at or before this character.
            var low = 0;
            var high = _replacements.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (_replacements[middle].ResultStart <= index) low = middle + 1;
                else high = middle - 1;
            }
            if (high < 0) return index;

            var replacement = _replacements[high];
            var afterLiteral = index - replacement.ResultStart - replacement.Literal.Length;
            return afterLiteral < 0 ? replacement.SourceStart : replacement.SourceStart + replacement.SourceLength + afterLiteral;
        }
    }

    public IEnumerator<int> GetEnumerator()
    {
        var source = 0;
        foreach (var replacement in _replacements)
        {
            while (source < replacement.SourceStart) yield return source++;
            for (var i = 0; i < replacement.Literal.Length; i++) yield return replacement.SourceStart;
            source = replacement.SourceStart + replacement.SourceLength;
        }
        while (source < _sourceLength) yield return source++;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
