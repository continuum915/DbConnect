using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DbConnect.Services;

public enum SqlBatchDialect
{
    Standard,
    PostgreSql,
    MySql,
    SqlServer,
    Sqlite
}

/// <summary>A statement's location in the original script, trimmed of surrounding whitespace.</summary>
public readonly record struct StatementSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>A script's GO counts ask for more commands than one Run will send.</summary>
public sealed class TooManyRepeatedCommandsException() : Exception(
    $"This script's GO counts ask for more than {SqlBatchParser.MaximumRepeatedCommands:N0} commands in one Run. Lower a GO count, or repeat the work with a WHILE loop.");

public static class SqlBatchParser
{
    /// <summary>Every command keeps a result until the next Run, so a typo in a GO count must not exhaust memory.</summary>
    public const int MaximumRepeatedCommands = 1_000_000;

    // Drivers convert a timeout to milliseconds, so stay well inside what that can hold.
    private const long MaximumTimeoutSeconds = int.MaxValue / 1000;

    public static IReadOnlyList<StatementSpan> GetStatementSpans(string sql, SqlBatchDialect dialect = SqlBatchDialect.Standard,
        CancellationToken cancellationToken = default) =>
        ScanStatements(sql, dialect, cancellationToken);

    /// <summary>
    /// The text Run submits as separate commands. On SQL Server that is a GO batch, because variables,
    /// procedure bodies and required terminators only work within one. Elsewhere it is a statement.
    /// In an open driver transaction, a batch with a typed COMMIT or ROLLBACK stays split into statements:
    /// the session can only end its transaction through the driver when one arrives as its own command.
    /// <c>GO n</c> repeats its batch's commands n times.
    /// </summary>
    /// <exception cref="TooManyRepeatedCommandsException">The script's GO counts ask for too many commands.</exception>
    public static IReadOnlyList<StatementSpan> GetExecutionSpans(string sql, SqlBatchDialect dialect, bool inDriverTransaction = false,
        CancellationToken cancellationToken = default)
    {
        if (dialect != SqlBatchDialect.SqlServer)
            return ScanStatements(sql, dialect, cancellationToken);

        var batches = new List<ScriptBatch>();
        var statements = ScanStatements(sql, dialect, cancellationToken, batches);
        var units = new List<StatementSpan>(batches.Count);
        var anyRepeat = false;
        foreach (var batch in batches)
        {
            var batchStatements = statements.GetRange(batch.FirstStatement, batch.StatementCount);
            var split = inDriverTransaction && batchStatements.Exists(span => TransactionControl.Classify(sql.Substring(span.Start, span.Length)) != TransactionEnd.None);
            anyRepeat |= batch.Repeat > 1;
            if (anyRepeat && units.Count + (long)batch.Repeat * (split ? batch.StatementCount : 1) > MaximumRepeatedCommands)
                throw new TooManyRepeatedCommandsException();
            for (var repeat = 0; repeat < batch.Repeat; repeat++)
            {
                if (split)
                    units.AddRange(batchStatements);
                else
                    units.Add(batch.Span);
            }
        }
        return units;
    }

    /// <summary>
    /// A timeout means one statement's limit, so a command holding several gets that limit for each.
    /// Otherwise a script with no GO lines would time out as a whole where each statement is quick.
    /// </summary>
    public static int ScaleTimeoutToStatements(int timeoutSeconds, string unit, SqlBatchDialect dialect)
    {
        if (timeoutSeconds <= 0 || dialect != SqlBatchDialect.SqlServer)
            return timeoutSeconds;
        var statements = ScanStatements(unit, dialect, CancellationToken.None).Count;
        return (int)Math.Min(MaximumTimeoutSeconds, (long)timeoutSeconds * Math.Max(1, statements));
    }

    public static IReadOnlyList<string> SplitStatements(string sql, SqlBatchDialect dialect = SqlBatchDialect.Standard, CancellationToken cancellationToken = default)
    {
        var spans = ScanStatements(sql, dialect, cancellationToken);
        var statements = new List<string>(spans.Count);
        foreach (var span in spans)
            statements.Add(sql.Substring(span.Start, span.Length));
        return statements;
    }

    /// <summary>
    /// Finds the statement the caret belongs to, using the same tokenizer as <see cref="SplitStatements"/>.
    /// A caret between statements (after a terminator, in blank space or on a <c>GO</c> line) belongs to the statement before it.
    /// </summary>
    public static StatementSpan? FindStatementAt(string sql, int caretOffset, SqlBatchDialect dialect = SqlBatchDialect.Standard,
        CancellationToken cancellationToken = default)
    {
        var spans = ScanStatements(sql, dialect, cancellationToken);
        if (spans.Count == 0)
            return null;

        caretOffset = Math.Clamp(caretOffset, 0, sql.Length);
        var found = spans[0];
        foreach (var span in spans)
        {
            if (span.Start > caretOffset)
                break;
            found = span;
        }

        return found;
    }

    private readonly record struct ScriptBatch(StatementSpan Span, int FirstStatement, int StatementCount, int Repeat);

    /// <summary>
    /// The statements, as <see cref="GetStatementSpans"/>, each with the delimiter it was read under, so a reader that
    /// cuts the script before a statement can go on under the same MySQL DELIMITER.
    /// </summary>
    internal static List<(StatementSpan Span, string Delimiter)> GetStatementSpansWithDelimiters(string sql, SqlBatchDialect dialect,
        CancellationToken cancellationToken)
    {
        var delimiters = new List<string>();
        var spans = ScanStatements(sql, dialect, cancellationToken, delimiters: delimiters);
        var paired = new List<(StatementSpan, string)>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
            paired.Add((spans[i], delimiters[i]));
        return paired;
    }

    private static List<StatementSpan> ScanStatements(string sql, SqlBatchDialect dialect, CancellationToken cancellationToken,
        List<ScriptBatch>? batches = null, List<string>? delimiters = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delimiter = ";";
        var statements = new List<StatementSpan>();
        var segmentStart = 0;
        var batchStart = 0;
        var batchFirstStatement = 0;
        var blockDepth = 0;
        var tracksBlocks = dialect is SqlBatchDialect.SqlServer or SqlBatchDialect.Sqlite;
        using var tokens = SqlTokenizer.EnumerateRanges(sql, dialect).GetEnumerator();
        var hasToken = tokens.MoveNext();
        var tokensRead = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            if ((i & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            while (hasToken && tokens.Current.Start < i)
            {
                hasToken = tokens.MoveNext();
                // Quoted text and comments skip offsets, so the offset check alone can miss for a long time.
                if ((++tokensRead & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            if (hasToken && tokens.Current.Start == i)
            {
                var token = tokens.Current;
                if (token.Kind == SqlTokenKind.Directive)
                {
                    if (dialect == SqlBatchDialect.SqlServer)
                    {
                        Flush(i);
                        TryReadGoLine(sql, i, out _, out var repeat);
                        EndBatch(i, repeat);
                        batchStart = i + token.Length;
                        blockDepth = 0;
                    }
                    else
                    {
                        // Text left without its terminator before the line is a statement of its own, not dropped.
                        Flush(i);
                        TryReadDelimiterCommand(sql, i, out delimiter, out _);
                    }
                    i += token.Length - 1;
                    segmentStart = i + 1;
                    continue;
                }
                if (token.Kind is SqlTokenKind.Quoted or SqlTokenKind.Comment)
                {
                    i += token.Length - 1;
                    continue;
                }
            }

            if (tracksBlocks && char.IsLetter(sql[i]) && (i == 0 || !IsWordPrefixChar(sql[i - 1])))
                blockDepth = UpdateBlockDepth(sql, i, blockDepth);

            if (blockDepth == 0 && string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                Flush(i);
                i += delimiter.Length - 1;
                segmentStart = i + 1;
            }
        }

        Flush(sql.Length);
        EndBatch(sql.Length, 1);
        return statements;

        void Flush(int end)
        {
            var trimmed = Trimmed(segmentStart, end);
            if (trimmed.Length > 0)
            {
                statements.Add(trimmed);
                delimiters?.Add(delimiter);
            }
        }

        // A batch of only terminators has no statements, and nothing to submit.
        void EndBatch(int end, int repeat)
        {
            if (batches != null && statements.Count > batchFirstStatement)
                batches.Add(new ScriptBatch(Trimmed(batchStart, end), batchFirstStatement, statements.Count - batchFirstStatement, repeat));
            batchFirstStatement = statements.Count;
        }

        StatementSpan Trimmed(int start, int end)
        {
            while (start < end && char.IsWhiteSpace(sql[start]))
                start++;
            while (end > start && char.IsWhiteSpace(sql[end - 1]))
                end--;
            return new StatementSpan(start, end - start);
        }
    }

    private static bool IsWordPrefixChar(char c) => IsIdentifierChar(c) || c == '@' || c == '#' || c == '$';

    internal static bool IsAtLineStart(string sql, int index)
    {
        for (var j = index - 1; j >= 0 && sql[j] != '\n'; j--)
        {
            if (sql[j] != ' ' && sql[j] != '\t' && sql[j] != '\r')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a T-SQL <c>GO [count]</c> line (optional trailing line comment), consuming through end of line.
    /// A count must be a positive whole number after whitespace. Anything else is not a GO line, so it stays
    /// in the batch and the server rejects it, rather than running the batch a different number of times.
    /// </summary>
    internal static bool TryReadGoLine(string sql, int start, out int consumed, out int repeat)
    {
        consumed = 0;
        repeat = 1;
        if (string.Compare(sql, start, "GO", 0, 2, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var end = sql.IndexOf('\n', start);
        if (end < 0)
            end = sql.Length;

        var rest = sql.AsSpan(start + 2, end - start - 2);
        var comment = rest.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0)
            rest = rest[..comment];
        if (!rest.IsWhiteSpace()
            && (!char.IsWhiteSpace(rest[0])
                || !int.TryParse(rest.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out repeat)
                || repeat < 1))
            return false;

        consumed = Math.Min(end + 1, sql.Length) - start;
        return true;
    }

    private static ReadOnlySpan<char> ReadWord(string sql, ref int index)
    {
        var start = index;
        while (index < sql.Length && IsIdentifierChar(sql[index]))
            index++;
        return sql.AsSpan(start, index - start);
    }

    /// <summary>
    /// Tracks BEGIN/CASE ... END nesting. <c>BEGIN TRAN[SACTION]</c>, <c>BEGIN DEFERRED|IMMEDIATE|EXCLUSIVE</c>
    /// and a bare <c>BEGIN;</c> start a transaction rather than a block.
    /// </summary>
    private static int UpdateBlockDepth(string sql, int start, int depth)
    {
        var index = start;
        var word = ReadWord(sql, ref index);
        if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            return depth + 1;

        if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0, depth - 1);

        if (!word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            return depth;

        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;
        if (index >= sql.Length || sql[index] == ';')
            return depth;

        var following = ReadWord(sql, ref index);
        var startsTransaction = following.Equals("TRAN", StringComparison.OrdinalIgnoreCase)
            || following.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
            || following.Equals("DISTRIBUTED", StringComparison.OrdinalIgnoreCase)
            || following.Equals("DEFERRED", StringComparison.OrdinalIgnoreCase)
            || following.Equals("IMMEDIATE", StringComparison.OrdinalIgnoreCase)
            || following.Equals("EXCLUSIVE", StringComparison.OrdinalIgnoreCase);
        return startsTransaction ? depth : depth + 1;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Reads a MySQL client <c>DELIMITER x</c> line at <paramref name="start"/>, consuming through end of line.</summary>
    internal static bool TryReadDelimiterCommand(string sql, int start, out string delimiter, out int consumed)
    {
        delimiter = "";
        consumed = 0;
        const string keyword = "DELIMITER";
        if (string.Compare(sql, start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var j = start + keyword.Length;
        if (j >= sql.Length || (sql[j] != ' ' && sql[j] != '\t'))
            return false;

        var end = sql.IndexOf('\n', j);
        if (end < 0)
            end = sql.Length;

        var value = sql.Substring(j, end - j).Trim();
        if (value.Length == 0)
            return false;

        delimiter = value;
        consumed = Math.Min(end + 1, sql.Length) - start;
        return true;
    }
}
