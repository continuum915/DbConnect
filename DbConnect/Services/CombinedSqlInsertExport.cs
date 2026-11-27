using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// How a combined export ended: the outcome for the file, rows written per table in the order the tables were given
/// (null for a table not written), and the index of the table that failed it, if one did.
/// </summary>
public sealed record CombinedExportResult(ExportOutcome Outcome, IReadOnlyList<long?> Rows, int? FailedTable);

/// <summary>
/// Export Data ▸ SQL INSERT ▸ one combined file: every ticked table's inserts, each optionally after its CREATE script,
/// in one <c>.sql</c> file with parents before the tables that reference them. Tables are read one after another on
/// one reserved session. A header states what the file holds and which referenced tables it expects to exist. The
/// file is published only when every table was written; a failed table or Cancel leaves nothing published.
/// </summary>
public static class CombinedSqlInsertExport
{
    private const int ProgressEvery = 1000;

    /// <param name="progress">Called with the index of the table being written, as given, and its rows so far.</param>
    public static async Task<CombinedExportResult> ExportAsync(DatabaseSession session, IReadOnlyList<DatabaseObjectIdentity> tables,
        ExportOptions chosen, string path, IExportFileOperations? files = null, Action<int, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var file = new OwnedExportFile(files ?? new LocalExportFileOperations(), path, DatabaseDumpService.FileType.Extension);
        var rows = new long?[tables.Count];
        int? current = null;
        string? diagnostic = null;
        var status = ExportStatus.Failed;
        try
        {
            await using var db = new ExportDbSession(await SessionSetup.OpenAsync(session, cancellationToken));
            var foreignKeys = await ForeignKeyService.LoadAsync(db);
            // Each table gets its own placeholder snapshot, so the order maps back to positions even for equal identities.
            var placeholders = tables.Select(t => new TableDefinitionSnapshot(t, session.ProviderName, [])).ToList();
            var order = DumpTableOrder.Sort(placeholders, foreignKeys);
            var indexes = order.Tables.Select(t => placeholders.FindIndex(p => ReferenceEquals(p, t))).ToList();
            await file.WriteAndPublishAsync(async stream =>
            {
                await WriteTextAsync(stream, Header(session, tables, indexes, order, foreignKeys, chosen), cancellationToken);
                foreach (var index in indexes)
                {
                    current = index;
                    var table = tables[index];
                    await WriteTextAsync(stream, $"\n-- Table {ObjectNames.SchemaQualified(table)}\n", cancellationToken);
                    var source = await TableExportSources.ForTableAsync(session, table, db);
                    long written = 0;
                    progress?.Invoke(index, 0);
                    await SqlInsertExportWriter.WriteAsync(stream, source, source.Columns,
                        TableExportSources.OptionsFor(chosen, ExportFormat.SqlInsert, session, table), () =>
                        {
                            if (++written % ProgressEvery == 0) progress?.Invoke(index, written);
                        }, cancellationToken);
                    rows[index] = written;
                    progress?.Invoke(index, written);
                }
                current = null;
            }, cancellationToken);
            status = ExportStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ExportStatus.Canceled; }
        catch (Exception error)
        {
            var reason = error is SqlInsertExportException ? error.Message : DiagnosticRedactor.Redact(error.Message);
            diagnostic = (current is { } index
                ? $"Table {ObjectNames.SchemaQualified(tables[index])} could not be exported: {reason}"
                : $"The export failed: {reason}") + " Destination was not published.";
        }
        var completed = status == ExportStatus.Completed;
        var outcome = new ExportOutcome(status, ExportScope.AllMatchingTableRows, ExportFormat.SqlInsert, completed ? rows.Sum(r => r ?? 0) : 0,
            clock.Elapsed, completed ? file.Destination : null, diagnostic, file.RetainedTemporaryPath);
        return new(outcome, completed ? rows : new long?[tables.Count], status == ExportStatus.Failed ? current : null);
    }

    private static string Header(DatabaseSession session, IReadOnlyList<DatabaseObjectIdentity> tables, IReadOnlyList<int> indexes,
        DumpTableOrder order, IReadOnlyList<ForeignKeyRelationship> foreignKeys, ExportOptions chosen)
    {
        var header = new StringBuilder();
        void Line(string text) => header.Append("-- ").Append(text.Replace("\r", "").Replace("\n", " ")).Append('\n');
        var names = indexes.Select(i => ObjectNames.SchemaQualified(tables[i])).ToList();
        Line("DbConnect SQL INSERT export");
        Line($"Provider: {session.ProviderName}");
        Line($"Database: {session.DatabaseLabel ?? session.Name}");
        Line($"Created: {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
        Line($"Includes: {tables.Count} table{(tables.Count == 1 ? "" : "s")} ({string.Join(", ", names)})" +
             (chosen.IncludeCreateTable ? ", CREATE TABLE" : "") + ", rows as INSERT statements");
        foreach (var cycle in order.Cycles.Where(c => !c.IsSelfReference))
            Line("Foreign-key cycle (the rows load only with foreign-key checks off; Dump SQL File handles cycles): " +
                 string.Join(", ", cycle.Tables.Select(t => ObjectNames.SchemaQualified(t.Table))));
        // A key whose referencing table is exported and whose referenced table is not.
        var exported = new DumpTableNames(tables);
        var referenced = foreignKeys.Where(k => exported.Find(k.Table) != null && exported.Find(k.ReferencedTable) == null)
            .Select(k => k.ReferencedTable).Distinct().ToList();
        if (referenced.Count > 0)
        {
            Line("Prerequisites (must already exist; not scripted):");
            foreach (var table in referenced) Line($"  Referenced table {table} (foreign key target)");
        }
        return header.ToString();
    }

    private static async Task WriteTextAsync(Stream output, string text, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }
}
