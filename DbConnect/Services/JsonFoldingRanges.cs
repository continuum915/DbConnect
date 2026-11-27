using System.Collections.Generic;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-18 — Fold regions for JSON text in the cell value viewer: every object or array whose
/// brackets sit on different lines. Brackets inside strings are skipped; the text needn't be valid.
/// </summary>
public static class JsonFoldingRanges
{
    /// <summary>Each region's opening bracket offset and closing bracket offset.</summary>
    public static IReadOnlyList<(int Start, int End)> Compute(string text)
    {
        var foldings = new List<(int Start, int End)>();
        var open = new Stack<(int Offset, int Line)>();
        var line = 0;
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
                line++;

            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    open.Push((i, line));
                    break;
                case '}' or ']' when open.Count > 0:
                    var (start, startLine) = open.Pop();
                    if (startLine != line)
                        foldings.Add((start, i));
                    break;
            }
        }

        return foldings;
    }
}
