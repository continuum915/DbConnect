using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Writes a dump's table data as INSERT statements, table by table on one reserved session and page by page, so no
/// table is held in memory. Where the engine offers a cheap snapshot (<see cref="DatabaseProviderCapabilities.SnapshotRead"/>)
/// every table is read inside it; <see cref="Snapshot"/> then names it for the header.
/// </summary>
internal sealed class DumpTableData(DatabaseSession session, ExportDbSession db)
{
    private const int PageSize = 5000;
    private const int ProgressEvery = 1000;
    // Set before the first Begin statement runs, so a start that fails halfway is still undone.
    private SnapshotReadDialect? _starting;
    private SnapshotReadDialect? _activeSnapshot;

    /// <summary>How the data is read in one snapshot, once <see cref="BeginAsync"/> started one; null when it is not one snapshot.</summary>
    public string? SnapshotDescription => _activeSnapshot?.Description;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (session.Capabilities.SnapshotRead is not { } dialect) return;
        if (dialect.Probe is { } probe)
        {
            var result = await db.ExecuteQueryAsync(probe, new QueryExecutionOptions { CancellationToken = cancellationToken });
            if (result.HasError || result.ObjectRows.Count == 0 || Convert.ToInt64(result.ObjectRows[0][0]) != 1) return;
        }
        _starting = dialect;
        foreach (var statement in dialect.Begin)
        {
            var result = await db.ExecuteQueryAsync(statement, new QueryExecutionOptions { CancellationToken = cancellationToken });
            if (result.HasError)
                throw new DatabaseDumpException($"The data snapshot could not be started: {DiagnosticRedactor.Redact(result.ErrorMessage ?? statement)}");
        }
        _activeSnapshot = dialect;
    }

    /// <summary>Closes the snapshot, or undoes a start that failed; the data was only read, so nothing is lost if this fails.</summary>
    public async Task EndAsync()
    {
        if (_starting is not { } dialect) return;
        _starting = _activeSnapshot = null;
        foreach (var statement in dialect.End)
            try { await db.ExecuteQueryAsync(statement); }
            catch (Exception) { /* The session is closed next, which ends the transaction anyway. */ }
    }

    /// <summary>Writes the table's rows to <paramref name="output"/>, reporting the running count, and returns it.</summary>
    public async Task<long> WriteAsync(DatabaseObjectIdentity table, Stream output, Action<long> progress, CancellationToken cancellationToken)
    {
        var name = ObjectNames.SchemaQualified(table);
        long rows = 0;
        try
        {
            var layout = await LayoutAsync(table, cancellationToken);
            var columns = layout.Columns;
            var loading = session.Capabilities.IdentityLoading;
            var source = layout.Source(db, table, PageSize);
            var target = SqlInsertTarget.ForTable(table, session.Capabilities);
            var identity = columns.Any(c => c.Identity != null);
            var options = new ExportOptions
            {
                Format = ExportFormat.SqlInsert,
                SqlDialect = session.Capabilities,
                SqlTableName = target,
                SqlInsertOverride = identity ? loading?.InsertOverride : null
            };
            progress(0);
            if (identity && loading?.Enable is { } enable) await WriteLineAsync(output, Format(enable, target, null), cancellationToken);
            await SqlInsertExportWriter.WriteAsync(output, source, source.Columns, options, () =>
            {
                if (++rows % ProgressEvery == 0) progress(rows);
            }, cancellationToken);
            if (identity && loading?.Disable is { } disable) await WriteLineAsync(output, Format(disable, target, null), cancellationToken);
            // An empty table's sequences are still where its CREATE script starts them.
            if (rows > 0 && loading?.Advance is { } advance)
                foreach (var column in columns.Where(c => c.Identity != null || c.Sequence is { Owned: true }))
                    await WriteLineAsync(output, Format(advance, target, column.Name), cancellationToken);
            if (rows % ProgressEvery != 0) progress(rows);
            return rows;
        }
        catch (SqlInsertExportException ex)
        {
            throw new DatabaseDumpException($"Table {name} could not be dumped: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DatabaseDumpException))
        {
            throw new DatabaseDumpException($"Table {name} could not be dumped: {DiagnosticRedactor.Redact(ex.Message)}", ex);
        }
    }

    /// <summary>
    /// Fails unless every row that references a row of its own table through <paramref name="key"/> is written after
    /// that row, for engines that check each INSERT and cannot turn checks off. A row whose place cannot be compared
    /// (a NULL sort column) counts as written too early.
    /// </summary>
    public async Task CheckLoadOrderAsync(DatabaseObjectIdentity table, ForeignKeyRelationship key, CancellationToken cancellationToken)
    {
        var name = ObjectNames.SchemaQualified(table);
        long early;
        try
        {
            var capabilities = session.Capabilities;
            var order = (await LayoutAsync(table, cancellationToken)).Order;
            string Row(string alias) => "(" + string.Join(", ", order.Select(c => $"{alias}.{capabilities.QuoteIdentifier(c)}")) + ")";
            var join = string.Join(" AND ", key.Columns.Zip(key.ReferencedColumns,
                (from, to) => $"c.{capabilities.QuoteIdentifier(from)} = p.{capabilities.QuoteIdentifier(to)}"));
            var target = SqlInsertTarget.ForTable(table, capabilities);
            var result = await db.ExecuteQueryAsync(
                $"SELECT count(*) FROM {target} c JOIN {target} p ON {join} WHERE NOT COALESCE({Row("p")} < {Row("c")}, false)",
                new QueryExecutionOptions { CancellationToken = cancellationToken });
            if (result.HasError) throw new InvalidOperationException(result.ErrorMessage);
            early = Convert.ToInt64(result.ObjectRows[0][0], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseDumpException($"Table {name} references itself, and the order of its rows could not be checked: " +
                                            DiagnosticRedactor.Redact(ex.Message), ex);
        }
        if (early > 0)
            throw new DatabaseDumpException($"Table {name} references itself, and {session.ProviderName} cannot turn off foreign-key checks: " +
                                            $"{early} row{(early == 1 ? " references a row" : "s reference rows")} the dump writes after " +
                                            $"{(early == 1 ? "it" : "them")}, so the script would not load.");
    }

    /// <summary>The columns the dump inserts, the columns it sorts the rows by, and how it pages them.</summary>
    private Task<TableRowLayout> LayoutAsync(DatabaseObjectIdentity table, CancellationToken cancellationToken) =>
        TableRowLayout.ReadAsync(session, table, cancellationToken);

    private string Format(string statement, string table, string? column) =>
        IdentityValueLoading.Format(statement, table, column, session.Capabilities);

    private static async Task WriteLineAsync(Stream output, string statement, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
        await writer.WriteAsync((statement + "\n").AsMemory(), cancellationToken);
    }
}
