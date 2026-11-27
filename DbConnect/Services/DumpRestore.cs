using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Restores a <see cref="BackupStrategy.DbConnectDump"/> backup, or any script Run accepts, by running the file statement by
/// statement on one session as <see cref="SqlScriptReader"/> reads it. Nothing wraps the statements in a transaction:
/// MySQL commits DDL as it runs, so a failure or cancel leaves what ran before it in place, and the error says so.
/// </summary>
internal static class DumpRestore
{
    // Statements run one at a time; a dump's INSERTs are short, but an index built over a large table is not, so each
    // statement gets an hour rather than the connection's query timeout.
    private const int StatementTimeoutSeconds = 3600;

    public static async Task RunAsync(DatabaseSession session, string path, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report($"Restoring {path} into {session.Name}…");
        var clock = Stopwatch.StartNew();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SqlScriptReader.DefaultChunkSize, useAsync: true);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        await using var db = await SessionSetup.OpenAsync(session, cancellationToken);
        var statements = 0;
        var reported = -1;
        long ranBytes = 0;
        try
        {
            await foreach (var statement in SqlScriptReader.ReadAsync(reader, session.Capabilities.BatchDialect, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Cancel is honoured between statements only: a statement that has started runs to its end, so the
                // count of statements applied is exact even where DDL commits as it runs.
                var result = await db.ExecuteQueryAsync(statement.Text, new QueryExecutionOptions
                {
                    TimeoutSeconds = StatementTimeoutSeconds,
                    MaxResultRows = 1,
                    IncludeColumnSchema = false
                });
                if (result.HasError)
                    throw new InvalidOperationException($"Statement {statement.Number} failed: {DiagnosticRedactor.Redact(FirstLine(statement.Text))} — " +
                                                        $"{DiagnosticRedactor.Redact(result.ErrorMessage ?? "unknown error")} {StillApplied(statement.Number)}");
                statements = statement.Number;
                // The stream reads ahead a buffer at a time, so count the statements' own bytes (and a terminator
                // each) instead; whitespace and comments make this run a little behind the file.
                ranBytes += Encoding.UTF8.GetByteCount(statement.Text) + 1;
                var percent = file.Length == 0 ? 99 : (int)Math.Min(99, ranBytes * 100 / file.Length);
                if (percent != reported)
                {
                    reported = percent;
                    progress.Report(string.Create(CultureInfo.InvariantCulture, $"{percent}% ({statements:N0} statements)"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Seen while reading or before a statement: the next statement did not run.
            throw new OperationCanceledException($"Restore cancelled before statement {statements + 1}. {StillApplied(statements + 1)}", cancellationToken);
        }
        progress.Report(string.Create(CultureInfo.InvariantCulture, $"Restore complete: {statements:N0} statements in {clock.Elapsed:mm\\:ss}."));
    }

    /// <summary>What a stop at statement <paramref name="number"/> leaves in the database.</summary>
    private static string StillApplied(int number) => number switch
    {
        1 => "No statement was applied.",
        2 => "Statement 1 was applied and stays applied.",
        _ => $"Statements 1–{number - 1} were applied and stay applied."
    };

    private static string FirstLine(string statement)
    {
        var end = statement.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? statement : statement[..end];
        if (line.Length > 120) line = line[..120];
        return line.Length < statement.Length ? line + "…" : line;
    }
}
