using System;

namespace DbConnect.Services;

/// <summary>How a statement ends the transaction a session has open.</summary>
public enum TransactionEnd
{
    /// <summary>The statement does not end a transaction.</summary>
    None,
    Commit,
    Rollback
}

/// <summary>
/// Recognises the statements a user types that end the transaction their session has open. A
/// session runs those through the driver's own commit and rollback instead of as SQL, so the
/// driver and the session cannot disagree about whether a transaction is still open.
/// </summary>
public static class TransactionControl
{
    /// <summary>
    /// COMMIT (with an optional TRANSACTION, TRAN or WORK) commits; ROLLBACK rolls back. A rollback to a
    /// savepoint leaves the transaction open, so it is not one. END is deliberately not recognised: only
    /// some databases accept it as a commit, and a bare END fragment left by splitting a trigger or
    /// procedure body must never be mistaken for one and commit the user's work.
    /// </summary>
    public static TransactionEnd Classify(string sql)
    {
        var position = 0;
        var first = NextWord(sql, ref position);
        if (first.IsEmpty)
            return TransactionEnd.None;

        var second = NextWord(sql, ref position);

        if (first.Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
            return second.IsEmpty || IsTransactionNoun(second) ? TransactionEnd.Commit : TransactionEnd.None;

        if (first.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            return second.Equals("TO", StringComparison.OrdinalIgnoreCase) ? TransactionEnd.None : TransactionEnd.Rollback;

        return TransactionEnd.None;
    }

    private static bool IsTransactionNoun(ReadOnlySpan<char> word) =>
        word.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("TRAN", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("WORK", StringComparison.OrdinalIgnoreCase);

    /// <summary>The next bare word, skipping whitespace, semicolons and comments; empty at the end.</summary>
    private static ReadOnlySpan<char> NextWord(string sql, ref int position)
    {
        while (position < sql.Length)
        {
            var c = sql[position];
            if (char.IsWhiteSpace(c) || c == ';')
            {
                position++;
            }
            else if (c == '-' && position + 1 < sql.Length && sql[position + 1] == '-')
            {
                while (position < sql.Length && sql[position] != '\n')
                    position++;
            }
            else if (c == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
            {
                var end = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        var start = position;
        while (position < sql.Length && char.IsLetter(sql[position]))
            position++;

        // A word glued to digits, quotes or underscores (COMMITTED_AT, 'COMMIT') is not a keyword.
        if (position < sql.Length && !char.IsWhiteSpace(sql[position]) && sql[position] != ';')
        {
            position = sql.Length;
            return ReadOnlySpan<char>.Empty;
        }

        return sql.AsSpan(start, position - start);
    }
}
