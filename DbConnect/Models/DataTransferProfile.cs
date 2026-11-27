using System;
using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>One side of a saved transfer: the connection, by id and name, and the database picked on its server. No password.</summary>
public sealed class DataTransferEndpoint
{
    public Guid? ConnectionId { get; set; }
    public string ConnectionName { get; set; } = string.Empty;
    /// <summary>The database picked on a connection that shows every database on its server; null for the connection's own.</summary>
    public string? Database { get; set; }
}

/// <summary>A table of a saved transfer, by schema and name, with its own target mode when it has one.</summary>
public sealed class DataTransferProfileTable
{
    public string? Schema { get; set; }
    public string Name { get; set; } = string.Empty;
    public DataTransferTargetMode? Mode { get; set; }
}

/// <summary>
/// A Data Transfer saved to run again from the wizard or from Automation: which connections and databases, which
/// tables, and every option. Connections are named, never their credentials, which stay in the credential store.
/// </summary>
public sealed class DataTransferProfile
{
    public string Name { get; set; } = string.Empty;
    public DataTransferEndpoint Source { get; set; } = new();
    public DataTransferEndpoint Target { get; set; } = new();
    public List<DataTransferProfileTable> Tables { get; set; } = [];
    public int BatchSize { get; set; } = new DataTransferOptions().BatchSize;
    public DataTransferTransactionMode Transaction { get; set; }
    public bool ContinueOnError { get; set; }
    public DataTransferTargetMode Mode { get; set; } = DataTransferTargetMode.CreateOrAppend;
    public bool IncludeIndexes { get; set; } = true;
    public bool IncludeForeignKeys { get; set; } = true;

    /// <summary>Whether <paramref name="table"/> is one of the profile's tables (by schema and name).</summary>
    public bool Includes(DatabaseObjectIdentity table) => Find(table) != null;

    private DataTransferProfileTable? Find(DatabaseObjectIdentity table) =>
        Tables.Find(t => string.Equals(t.Name, table.Name, StringComparison.Ordinal) &&
                         string.Equals(t.Schema ?? "", table.Schema ?? "", StringComparison.Ordinal));

    /// <summary>The options for running the profile on <paramref name="tables"/>, the source's own identities of its tables.</summary>
    public DataTransferOptions ToOptions(IEnumerable<DatabaseObjectIdentity> tables)
    {
        var modes = new Dictionary<DatabaseObjectIdentity, DataTransferTargetMode>();
        foreach (var table in tables)
            if (Find(table)?.Mode is { } mode) modes[table] = mode;
        return new DataTransferOptions
        {
            BatchSize = Math.Max(1, BatchSize),
            Transaction = Transaction,
            ContinueOnError = ContinueOnError,
            Mode = Mode,
            IncludeIndexes = IncludeIndexes,
            IncludeForeignKeys = IncludeForeignKeys,
            TableModes = modes
        };
    }
}
