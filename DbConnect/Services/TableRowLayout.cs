using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// How a table's rows are read to be written somewhere else (a dump, a data transfer): the columns that take values,
/// the columns the rows sort by, and how they are paged. Generated columns are computed by the table they are written
/// to, and rowversion-like ones written by the engine; both refuse values. Rows sort by the primary key and are read
/// past the last key read (<see cref="TableKeySeek"/>), so a table is read in full even while it is written to. A table
/// without a primary key sorts by every column the engine can compare (PostgreSQL cannot sort json, SQL Server xml)
/// and is read by OFFSET.
/// </summary>
internal sealed record TableRowLayout(IReadOnlyList<TableDesignColumn> Columns, IReadOnlyList<string> Order, TableKeySeek? Seek)
{
    /// <param name="includeGenerated">Read generated columns too, for a target that holds their values as plain columns.</param>
    public static async Task<TableRowLayout> ReadAsync(DatabaseSession session, DatabaseObjectIdentity table,
        CancellationToken cancellationToken, bool includeGenerated = false)
    {
        if (session.Provider is not ITableDesignSource designs)
            throw new InvalidOperationException($"{session.ProviderName} cannot read table designs.");
        var design = await designs.GetTableDesignAsync(session.ConnectionString, table, cancellationToken);
        var loading = session.Capabilities.IdentityLoading;
        var columns = design.Columns.Where(c => (includeGenerated || c.Generated == null) &&
            loading?.EngineWrittenTypeNames.Contains(c.DataType, StringComparer.OrdinalIgnoreCase) != true).ToList();
        if (columns.Count == 0) throw new InvalidOperationException("The table has no columns to insert.");
        if (design.PrimaryKey?.Columns is { Count: > 0 } key)
        {
            var keyColumns = key.Select(k => design.Columns.First(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
            return new(columns, key, TableKeySeek.For(session.Capabilities, keyColumns));
        }
        var comparable = columns.Where(c => session.Capabilities.CanCompareType(c.DataType)).Select(c => c.Name).ToList();
        return new(columns, comparable.Count > 0 ? comparable : columns.Select(c => c.Name).ToList(), null);
    }

    /// <summary>The table's rows in this layout, page by page on <paramref name="db"/>, which the reading reserves.</summary>
    public TableExportSource Source(ExportDbSession db, DatabaseObjectIdentity table, int pageSize) =>
        new(_ => Task.FromResult<IDbSession>(db), table.Name,
            new TableQuerySpec(Order.Select(c => new TableSortKey(c)).ToList(), 0, pageSize) { Object = table },
            Columns.Select((c, i) => new ExportColumn(c.Name, i, c.DataType)), Seek);
}
