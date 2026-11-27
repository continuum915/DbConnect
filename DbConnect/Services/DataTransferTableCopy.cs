using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Copies one table for <see cref="DataTransferJob"/> as its <see cref="DataTransferTablePlan"/> says. With one
/// transaction per table, the table's creation (where the engine's DDL is transactional), rows, identity handling and
/// remaining definition commit together or not at all; its indexes follow, each on its own, and one that fails is
/// reported while the data stays. Where DDL commits on its own (MySQL) the created table stays even if its rows roll
/// back, and its rows commit before the definition's remaining parts, which would commit them anyway. Per batch, every
/// batch commits on its own, and with continue-on-error a rejected batch is retried row by row so only the rows the
/// target rejects are skipped. Between engines each value is converted for the target, and a value it cannot hold
/// fails its row (or skips it) with the column named.
/// </summary>
/// <param name="table">The table as it was chosen, which per-table modes are keyed by.</param>
internal sealed class DataTransferTableCopy(DatabaseSession source, DatabaseSession target, ExportDbSession reader, IDbSession writer,
    DatabaseObjectIdentity table, DataTransferTablePlan plan, DataTransferOptions options, Action<long> progress)
{
    private const int PageSize = 5000;
    private readonly List<DataTransferSkippedRow> _skipped = [];
    private readonly List<string> _notes = [];
    private bool PerTable => options.Transaction == DataTransferTransactionMode.PerTable;
    private bool ContinueOnError => !PerTable && options.ContinueOnError;
    private string Name => plan.TargetName;

    /// <summary>Rows the target holds or will hold on commit: committed batches, or the open table transaction's rows.</summary>
    private long _written;
    private long _read;
    private long _skippedCount;
    /// <summary>The created table outlives a rollback: it was created outside the table's transaction.</summary>
    private bool _createdOutsideTransaction;
    /// <summary>With one transaction per table, its rows committed before the remaining definition (non-transactional DDL).</summary>
    private bool _rowsCommitted;
    private string? _identityOff;
    /// <summary>What a drop or an emptying outside the table's transaction did, which a rollback cannot undo.</summary>
    private string? _changedOutsideTransaction;

    /// <summary>
    /// TRUNCATE where the engine has one that a transaction can undo and that nothing refuses (PostgreSQL, DuckDB);
    /// DELETE elsewhere, which keeps MySQL's implicit commit and SQL Server's foreign-key refusal out of the transfer.
    /// </summary>
    private string EmptySql() =>
        TableMaintenanceDialects.For(target.ProviderName) is { TruncateNote: null } maintenance &&
        maintenance.Truncate(plan.Target) != null && target.Capabilities.TransactionalDdl
            ? $"TRUNCATE TABLE {Name}"
            : $"DELETE FROM {Name}";

    public async Task<DataTransferTableOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var layout = await TableRowLayout.ReadAsync(source, table, cancellationToken, includeGenerated: plan.Columns != null);
            var dialect = target.Capabilities;
            var exists = await ExistsAsync(cancellationToken);
            var mode = options.ModeFor(table);
            if (exists && mode == DataTransferTargetMode.Create)
                return Outcome(DataTransferTableStatus.Failed, 0, clock, "The target table already exists, so it was left unchanged.");
            var recreate = exists && mode == DataTransferTargetMode.DropAndRecreate;
            var empty = exists && mode == DataTransferTargetMode.Empty;
            var create = !exists || recreate;
            if (create) _notes.AddRange(plan.Notes);
            var loading = dialect.IdentityLoading;
            // Identity columns take their source values only with the engine's override; serial columns take them as they are.
            var identity = layout.Columns.Any(c => c.Identity != null) ? loading : null;
            var insert = new DataTransferInsert(dialect, Name, layout.Columns.Select(c => c.Name).ToList(), options.BatchSize,
                identity?.InsertOverride, TargetTypes(layout));
            var convert = Converters(layout);

            if (PerTable && (dialect.TransactionalDdl || !create)) await writer.BeginTransactionAsync(cancellationToken);
            if (recreate)
            {
                _changedOutsideTransaction = !writer.IsInTransaction ? "The existing table was dropped." : null;
                await ExecuteAsync($"DROP TABLE {Name}", "The existing table", "dropped");
            }
            if (create)
            {
                _createdOutsideTransaction = !writer.IsInTransaction;
                await RunAsync(plan.BeforeData);
            }
            if (PerTable && !writer.IsInTransaction) await writer.BeginTransactionAsync(cancellationToken);
            if (empty)
            {
                _changedOutsideTransaction = !writer.IsInTransaction ? "The table's existing rows were removed." : null;
                await ExecuteAsync(EmptySql(), "The existing rows", "removed");
            }
            if (identity?.Enable is { } enable)
            {
                _identityOff = identity.Disable;
                await ExecuteAsync(IdentityValueLoading.Format(enable, Name, null, dialect), "Identity values");
            }
            progress(0);
            await CopyRowsAsync(layout, insert, convert, cancellationToken);
            await IdentityOffAsync();
            if (create)
            {
                if (PerTable && !dialect.TransactionalDdl)
                {
                    await writer.CommitAsync(cancellationToken);
                    _rowsCommitted = true;
                }
                await RunAsync(plan.AfterData);
            }
            // After the definition's remaining parts, which tie a serial column's sequence to it (OWNED BY). An empty
            // table's sequences are still where its definition starts them.
            if (_written > 0 && loading?.Advance is { } advance)
                foreach (var column in layout.Columns.Where(c => c.Identity != null || c.Sequence is { Owned: true }))
                    await ExecuteAsync(IdentityValueLoading.Format(advance, Name, column.Name, dialect), $"Sequence of {column.Name}");
            cancellationToken.ThrowIfCancellationRequested();
            if (writer.IsInTransaction) await writer.CommitAsync(cancellationToken);
            if (create) await AddIndexesAsync();
            var status = recreate ? DataTransferTableStatus.Recreated
                : empty ? DataTransferTableStatus.Emptied
                : create ? DataTransferTableStatus.Created : DataTransferTableStatus.Appended;
            return Outcome(status, _written, clock, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome(DataTransferTableStatus.Canceled, Kept, clock, $"Canceled after {_read} rows." + await UndoAsync());
        }
        catch (Exception ex)
        {
            var reason = ex is TableDefinitionUnavailableException or DataTransferException ? ex.Message : DiagnosticRedactor.Redact(ex.Message);
            return Outcome(DataTransferTableStatus.Failed, Kept, clock, reason + await UndoAsync());
        }
    }

    /// <summary>Each index once the table is committed, on its own: one the target refuses is reported, and the data stays.</summary>
    private async Task AddIndexesAsync()
    {
        if (!options.IncludeIndexes)
        {
            if (plan.Indexes.Count > 0) _notes.Add("Indexes were not created (turned off).");
            return;
        }
        foreach (var index in plan.Indexes)
        {
            var result = await writer.ExecuteTransactionAsync([new SqlCommandSpec(index.Sql, new Dictionary<string, object?>())]);
            if (!result.Succeeded) _notes.Add($"Index {index.Name} was not created: {DiagnosticRedactor.Redact(result.ErrorMessage ?? "")}");
        }
    }

    /// <summary>The target's types for typed NULLs: the column's own between one engine's connections, its portable type's binary family otherwise.</summary>
    private List<string> TargetTypes(TableRowLayout layout) => plan.Columns == null
        ? layout.Columns.Select(c => c.DataType).ToList()
        : layout.Columns.Select(c => Portable(c.Name)?.Type is PortableType.Binary or PortableType.VarBinary or PortableType.Blob ? "varbinary" : c.DataType).ToList();

    private PortableColumn? Portable(string column) =>
        plan.Columns?.FirstOrDefault(p => string.Equals(p.Name, column, StringComparison.OrdinalIgnoreCase));

    private Func<object?, object?>[]? Converters(TableRowLayout layout) => plan.Columns == null ? null
        : layout.Columns.Select(c => Portable(c.Name) is { } column ? PortableValueConverter.For(column, target.ProviderName) : v => v).ToArray();

    /// <summary>Rows that stay on the target once an open transaction rolls back.</summary>
    private long Kept => PerTable && !_rowsCommitted ? 0 : _written;

    private DataTransferTableOutcome Outcome(DataTransferTableStatus status, long rows, Stopwatch clock, string? diagnostic) =>
        new(table, status, rows, clock.Elapsed, diagnostic) { Notes = _notes.ToList(), RowsSkipped = _skippedCount, Skipped = _skipped.ToList() };

    private async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        var probe = await writer.ExecuteQueryAsync($"SELECT * FROM {Name} WHERE 1 = 0",
            new QueryExecutionOptions { CancellationToken = cancellationToken, IncludeColumnSchema = false });
        return !probe.HasError;
    }

    private async Task RunAsync(IEnumerable<DataTransferStatement> statements)
    {
        foreach (var statement in statements) await ExecuteAsync(statement.Sql, $"{statement.Kind} {statement.Name}");
    }

    /// <summary>Runs one statement in the open transaction, or in its own when none is open.</summary>
    private async Task ExecuteAsync(string sql, string what, string verb = "created")
    {
        var result = await writer.ExecuteTransactionAsync([new SqlCommandSpec(sql, new Dictionary<string, object?>())]);
        if (!result.Succeeded) throw new DataTransferException($"{what} could not be {verb}: {DiagnosticRedactor.Redact(result.ErrorMessage ?? "")}");
    }

    private async Task CopyRowsAsync(TableRowLayout layout, DataTransferInsert insert, Func<object?, object?>[]? convert,
        CancellationToken cancellationToken)
    {
        var keys = layout.Seek == null ? [] : layout.Order
            .Select(k => (Name: k, Ordinal: layout.Columns.ToList().FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))))
            .Where(k => k.Ordinal >= 0).ToList();
        var batchSize = Math.Max(1, options.BatchSize);
        var batch = new List<(long Number, IReadOnlyList<object?> Row)>(batchSize);
        await foreach (var row in layout.Source(reader, table, PageSize).ReadRowsAsync(cancellationToken))
        {
            var number = ++_read;
            if (Convert(row, convert, number, keys) is not { } values) continue;
            batch.Add((number, values));
            if (batch.Count < batchSize) continue;
            await WriteBatchAsync(batch, insert, keys, cancellationToken);
        }
        if (batch.Count > 0) await WriteBatchAsync(batch, insert, keys, cancellationToken);
    }

    /// <summary>The row with every value converted for the target; null when continue-on-error skips a row with a value the target cannot hold.</summary>
    private IReadOnlyList<object?>? Convert(IReadOnlyList<object?> row, Func<object?, object?>[]? convert, long number,
        IReadOnlyList<(string Name, int Ordinal)> keys)
    {
        if (convert == null) return row;
        try
        {
            var values = new object?[row.Count];
            for (var i = 0; i < row.Count; i++) values[i] = i < convert.Length ? convert[i](row[i]) : row[i];
            return values;
        }
        catch (DataTransferValueException ex)
        {
            if (!ContinueOnError) throw new DataTransferException($"Row {number} was rejected: {ex.Message}");
            Skip(number, Key(row, keys), ex.Message);
            return null;
        }
    }

    private void Skip(long number, string key, string error)
    {
        _skippedCount++;
        if (_skipped.Count < DataTransferOptions.MaxSkippedRowsReported) _skipped.Add(new(number, key, error));
    }

    private async Task WriteBatchAsync(List<(long Number, IReadOnlyList<object?> Row)> batch, DataTransferInsert insert,
        IReadOnlyList<(string Name, int Ordinal)> keys, CancellationToken cancellationToken)
    {
        var commands = insert.Commands(batch.Select(b => b.Row).ToList());
        if (PerTable)
        {
            var result = await writer.ExecuteTransactionAsync(commands);
            if (!result.Succeeded) throw Rejected(result, batch, insert.RowsPerStatement);
            _written += batch.Count;
        }
        else
        {
            await writer.BeginTransactionAsync(cancellationToken);
            var result = await writer.ExecuteTransactionAsync(commands);
            if (result.Succeeded)
            {
                await writer.CommitAsync(cancellationToken);
                _written += batch.Count;
            }
            else
            {
                await writer.RollbackAsync(cancellationToken);
                if (!ContinueOnError) throw Rejected(result, batch, insert.RowsPerStatement);
                await RetryRowByRowAsync(batch, insert, keys, cancellationToken);
            }
        }
        batch.Clear();
        progress(_written);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Each row of a rejected batch in a transaction of its own; the rows the target still rejects are skipped.</summary>
    private async Task RetryRowByRowAsync(List<(long Number, IReadOnlyList<object?> Row)> batch, DataTransferInsert insert,
        IReadOnlyList<(string Name, int Ordinal)> keys, CancellationToken cancellationToken)
    {
        foreach (var (number, row) in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.BeginTransactionAsync(cancellationToken);
            var result = await writer.ExecuteTransactionAsync([insert.Command(row)]);
            if (result.Succeeded)
            {
                await writer.CommitAsync(cancellationToken);
                _written++;
                continue;
            }
            await writer.RollbackAsync(cancellationToken);
            Skip(number, Key(row, keys), DiagnosticRedactor.Redact(result.ErrorMessage ?? "Rejected."));
        }
    }

    /// <summary>Which rows a failed batch held: the statement that failed is the one after those that ran.</summary>
    private static DataTransferException Rejected(TransactionResult result, List<(long Number, IReadOnlyList<object?> Row)> batch, int perStatement)
    {
        var start = Math.Min(batch.Count - 1, result.CommandRecordsAffected.Count * perStatement);
        var end = Math.Min(batch.Count - 1, start + perStatement - 1);
        var (from, to) = (batch[start].Number, batch[end].Number);
        var rows = from == to ? $"Row {from} was rejected" : $"One of rows {from}–{to} was rejected";
        return new DataTransferException($"{rows}: {DiagnosticRedactor.Redact(result.ErrorMessage ?? "")}");
    }

    private static string Key(IReadOnlyList<object?> row, IReadOnlyList<(string Name, int Ordinal)> keys) =>
        string.Join(", ", keys.Select(k => $"{k.Name}={Show(k.Ordinal < row.Count ? row[k.Ordinal] : null)}"));

    private static string Show(object? value)
    {
        var text = value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => $"<{bytes.Length} bytes>",
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        return text.Length <= 64 ? text : text[..64] + "…";
    }

    private async Task IdentityOffAsync()
    {
        if (_identityOff is not { } off) return;
        _identityOff = null;
        await ExecuteAsync(IdentityValueLoading.Format(off, Name, null, target.Capabilities), "Identity values");
    }

    /// <summary>Rolls back the open transaction and turns identity loading off; says what that left on the target.</summary>
    private async Task<string> UndoAsync()
    {
        var text = "";
        if (writer.IsInTransaction)
        {
            try
            {
                await writer.RollbackAsync();
                text = PerTable && !_rowsCommitted ? " The table's changes were rolled back." : " The batch in progress was rolled back.";
            }
            catch (Exception ex)
            {
                return $" The rollback failed, so check the target table: {DiagnosticRedactor.Redact(ex.Message)}";
            }
        }
        try { await IdentityOffAsync(); }
        catch (Exception) { /* Identity loading lasts only as long as the transfer's session, which closes after the last table. */ }
        if (_changedOutsideTransaction != null) text += " " + _changedOutsideTransaction;
        if (_createdOutsideTransaction) text += $" The created table stays on the target with {Kept} rows.";
        else if (Kept > 0) text += $" {Kept} rows from earlier batches were kept.";
        return text;
    }
}
