namespace DbConnect.Services;

public static class SqlCompletionGuard
{
    /// <summary>
    /// Returns true when the caret is inside a string literal, line comment,
    /// or block comment and auto-complete should be suppressed.
    /// </summary>
    public static bool IsInsideStringOrComment(string text, int caretOffset)
    {
        // Clamp to valid range
        if (caretOffset > text.Length) caretOffset = text.Length;
        var prefix = text.Substring(0, caretOffset);

        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        int i = 0;
        while (i < prefix.Length)
        {
            if (inLineComment)
            {
                if (prefix[i] == '\n') inLineComment = false;
                i++;
                continue;
            }

            if (inBlockComment)
            {
                if (i + 1 < prefix.Length && prefix[i] == '*' && prefix[i + 1] == '/')
                {
                    inBlockComment = false;
                    i += 2;
                }
                else
                {
                    i++;
                }
                continue;
            }

            if (inSingleQuote)
            {
                // Escaped single-quote inside string: ''
                if (prefix[i] == '\'' && i + 1 < prefix.Length && prefix[i + 1] == '\'')
                {
                    i += 2;
                }
                else if (prefix[i] == '\'')
                {
                    inSingleQuote = false;
                    i++;
                }
                else
                {
                    i++;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (prefix[i] == '"' && i + 1 < prefix.Length && prefix[i + 1] == '"')
                {
                    i += 2;
                }
                else if (prefix[i] == '"')
                {
                    inDoubleQuote = false;
                    i++;
                }
                else
                {
                    i++;
                }
                continue;
            }

            // Not inside anything, check for start of comments or strings
            if (i + 1 < prefix.Length && prefix[i] == '-' && prefix[i + 1] == '-')
            {
                inLineComment = true;
                i += 2;
            }
            else if (i + 1 < prefix.Length && prefix[i] == '/' && prefix[i + 1] == '*')
            {
                inBlockComment = true;
                i += 2;
            }
            else if (prefix[i] == '\'')
            {
                inSingleQuote = true;
                i++;
            }
            else if (prefix[i] == '"')
            {
                inDoubleQuote = true;
                i++;
            }
            else
            {
                i++;
            }
        }

        return inSingleQuote || inDoubleQuote || inLineComment || inBlockComment;
    }
}
