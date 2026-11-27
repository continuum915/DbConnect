using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>A statement of a script file, numbered from 1 in file order.</summary>
public sealed record SqlScriptStatement(int Number, string Text);

/// <summary>
/// Reads a script statement by statement as <see cref="SqlBatchParser.SplitStatements"/> splits it, without holding
/// the file in memory. Text is read a chunk at a time; every statement before the last one in the buffer is complete
/// and is returned, and the buffer is cut where the last one starts. The MySQL DELIMITER in force there is written in
/// front of what remains, so the cut does not change how the rest is split.
/// </summary>
public static class SqlScriptReader
{
    public const int DefaultChunkSize = 1 << 16;

    /// <exception cref="NotSupportedException">SQL Server, whose scripts run in GO batches rather than statements.</exception>
    public static async IAsyncEnumerable<SqlScriptStatement> ReadAsync(TextReader reader, SqlBatchDialect dialect,
        [EnumeratorCancellation] CancellationToken cancellationToken, int chunkSize = DefaultChunkSize)
    {
        if (dialect == SqlBatchDialect.SqlServer)
            throw new NotSupportedException("SQL Server scripts run in GO batches, which this reader does not form.");
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var buffer = new StringBuilder();
        var chunk = new char[chunkSize];
        var number = 0;
        // Grows while one statement fills the buffer, so a long statement is not rescanned once per chunk.
        var wanted = chunkSize;
        while (true)
        {
            var atEnd = false;
            for (var read = 0; read < wanted;)
            {
                var count = await reader.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, wanted - read)), cancellationToken);
                if (count == 0)
                {
                    atEnd = true;
                    break;
                }
                buffer.Append(chunk, 0, count);
                read += count;
            }

            var text = buffer.ToString();
            var spans = SqlBatchParser.GetStatementSpansWithDelimiters(text, dialect, cancellationToken);
            var complete = atEnd ? spans.Count : spans.Count - 1;
            for (var i = 0; i < complete; i++)
                yield return new SqlScriptStatement(++number, text.Substring(spans[i].Span.Start, spans[i].Span.Length));
            if (atEnd) yield break;
            if (complete <= 0)
            {
                wanted = checked(wanted * 2);
                continue;
            }

            var (last, delimiter) = spans[^1];
            buffer.Clear();
            if (delimiter != ";") buffer.Append("DELIMITER ").Append(delimiter).Append('\n');
            // A statement that started mid-line must not start a line once cut, or a leading DELIMITER would become a
            // directive. A vertical tab is whitespace, so the statement's text is unchanged, but not line-start space.
            if (!SqlBatchParser.IsAtLineStart(text, last.Start)) buffer.Append('\v');
            buffer.Append(text, last.Start, text.Length - last.Start);
            wanted = chunkSize;
        }
    }
}
