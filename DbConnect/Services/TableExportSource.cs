using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Captures browsing context without capturing the database rows or counting them.</summary>
public sealed class TableExportSource : IExportSource
{
    private readonly Func<CancellationToken, Task<IDbSession>> _open;
    private readonly string _table;
    private readonly TableQuerySpec _query;
    private readonly TableKeySeek? _seek;
    public IReadOnlyList<ExportColumn> Columns { get; }

    /// <param name="seek">Pages by the table's key instead of by OFFSET; the query then sorts by that key, ascending.</param>
    public TableExportSource(Func<CancellationToken, Task<IDbSession>> open, string table, TableQuerySpec query, IEnumerable<ExportColumn> columns,
        TableKeySeek? seek = null)
    {
        _seek = seek;
        _open = open;
        _table = table;
        _query = query with
        {
            Sort = (seek?.Sort ?? query.Sort).ToArray(),
            Filters = query.Filters.Select(f => f with { ParameterValue = f.ParameterValue is byte[] bytes ? bytes.Clone() : f.ParameterValue }).ToArray()
        };
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = await _open(cancellationToken);
        if (session is not ExportDbSession exclusive) throw new InvalidOperationException("Export requires a reserved table session.");
        var source = new ReservedSource(exclusive, _table, _query, Columns, _seek);
        await foreach (var row in new BackgroundExportSource(source).ReadRowsAsync(cancellationToken)) yield return row;
    }

    private sealed record ReservedSource(ExportDbSession Session, string Table, TableQuerySpec Query,
        IReadOnlyList<ExportColumn> Columns, TableKeySeek? Seek) : IExportSource
    {
        public IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync(CancellationToken cancellationToken) =>
            Session.ReadTableAsync(Table, Query, Columns, Seek, cancellationToken);
    }
}
