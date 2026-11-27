using System;
using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>What became of one table in a data transfer.</summary>
public enum DataTransferTableStatus
{
    /// <summary>The target table did not exist; it was created and the rows copied into it.</summary>
    Created,
    /// <summary>The target table existed; the rows were added to it.</summary>
    Appended,
    /// <summary>The target table existed; it was dropped, created from the source's definition and filled.</summary>
    Recreated,
    /// <summary>The target table existed; its rows were removed and the source's inserted.</summary>
    Emptied,
    /// <summary>The table did not finish. With one transaction per table nothing was kept; per batch, the batches before the failure were.</summary>
    Failed,
    /// <summary>Stopped by the user before or while this table was copied; what was kept is as for <see cref="Failed"/>.</summary>
    Canceled
}

/// <summary>What happens to a target table before the rows go in, as Navicat's Data Transfer offers it.</summary>
public enum DataTransferTargetMode
{
    /// <summary>Create the table; a table that already exists fails and is left unchanged.</summary>
    Create,
    /// <summary>Create a missing table; add the rows to one that exists.</summary>
    CreateOrAppend,
    /// <summary>Drop a table that exists and create it again from the source's definition.</summary>
    DropAndRecreate,
    /// <summary>Remove an existing table's rows first (TRUNCATE where it is undoable and unconditional, DELETE otherwise).</summary>
    Empty
}

/// <summary>How much of a table one target transaction holds.</summary>
public enum DataTransferTransactionMode
{
    /// <summary>The whole table, with its creation where the engine's DDL is transactional: it arrives whole or not at all.</summary>
    PerTable,
    /// <summary>Each batch of rows commits on its own; a failure keeps the batches before it.</summary>
    PerBatch
}

/// <summary>A row the target rejected under continue-on-error: its 1-based position in the source, its key, and why.</summary>
public sealed record DataTransferSkippedRow(long RowNumber, string Key, string Error);

/// <summary>A table's outcome: rows kept on the target, time taken, and why it failed or what was not reproduced.</summary>
public sealed record DataTransferTableOutcome(DatabaseObjectIdentity Table, DataTransferTableStatus Status, long RowsCopied,
    TimeSpan Elapsed, string? Diagnostic = null)
{
    /// <summary>Notes from the table's definition: prerequisites that must already exist and behavior not reproduced.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Rows the target rejected and continue-on-error skipped.</summary>
    public long RowsSkipped { get; init; }

    /// <summary>The first <see cref="DataTransferOptions.MaxSkippedRowsReported"/> skipped rows, in source order.</summary>
    public IReadOnlyList<DataTransferSkippedRow> Skipped { get; init; } = [];
}

/// <summary>Progress of the table at <paramref name="TableIndex"/>: rows written so far.</summary>
public sealed record DataTransferProgress(int TableIndex, DatabaseObjectIdentity Table, long Rows);

public sealed record DataTransferOptions
{
    public const int MaxSkippedRowsReported = 1000;

    /// <summary>Rows sent to the target per round trip, as multi-row INSERTs within the provider's parameter limit.</summary>
    public int BatchSize { get; init; } = 500;

    public DataTransferTransactionMode Transaction { get; init; } = DataTransferTransactionMode.PerTable;

    /// <summary>Create a created table's indexes after its rows.</summary>
    public bool IncludeIndexes { get; init; } = true;

    /// <summary>Add the foreign keys of created tables that are separate statements, once every table's rows are in.</summary>
    public bool IncludeForeignKeys { get; init; } = true;

    /// <summary>The target mode for every table without its own in <see cref="TableModes"/>.</summary>
    public DataTransferTargetMode Mode { get; init; } = DataTransferTargetMode.CreateOrAppend;

    /// <summary>Per-table overrides of <see cref="Mode"/>, by source table.</summary>
    public IReadOnlyDictionary<DatabaseObjectIdentity, DataTransferTargetMode> TableModes { get; init; } =
        new Dictionary<DatabaseObjectIdentity, DataTransferTargetMode>();

    public DataTransferTargetMode ModeFor(DatabaseObjectIdentity table) => TableModes.TryGetValue(table, out var mode) ? mode : Mode;

    /// <summary>Whether a table's mode can remove data that is already on the target.</summary>
    public static bool IsDestructive(DataTransferTargetMode mode) =>
        mode is DataTransferTargetMode.DropAndRecreate or DataTransferTargetMode.Empty;

    /// <summary>
    /// With <see cref="DataTransferTransactionMode.PerBatch"/>, a rejected batch is retried row by row and the rows the
    /// target still rejects are skipped and reported. Ignored per table, where one rejected row undoes the table.
    /// </summary>
    public bool ContinueOnError { get; init; }
}
