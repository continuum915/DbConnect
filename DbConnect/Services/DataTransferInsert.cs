using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Parameterized INSERTs of a table's rows for a data transfer: multi-row VALUES lists of at most
/// <see cref="RowsPerStatement"/> rows, which keeps each command within the target's VALUES-list and parameter limits.
/// Where the target needs it to take a table's identity values (PostgreSQL), the override goes before VALUES, and NULLs
/// are bound with the type the target needs for the column where an untyped NULL is refused.
/// </summary>
internal sealed class DataTransferInsert
{
    private readonly DatabaseProviderCapabilities _dialect;
    private readonly string _prefix;
    private readonly int _columns;

    private readonly IReadOnlyList<object?> _nulls;

    /// <param name="columnTypes">Each column's declared type, for the dialect's <see cref="DatabaseProviderCapabilities.TypedNull"/>.</param>
    public DataTransferInsert(DatabaseProviderCapabilities dialect, string table, IReadOnlyList<string> columns, int batchSize,
        string? insertOverride = null, IReadOnlyList<string>? columnTypes = null)
    {
        _nulls = columns.Select((_, i) => columnTypes != null && i < columnTypes.Count ? dialect.TypedNull?.Invoke(columnTypes[i]) : null).ToList();
        if (columns.Count == 0) throw new ArgumentException("A table needs columns to insert.", nameof(columns));
        _dialect = dialect;
        _columns = columns.Count;
        _prefix = $"INSERT INTO {table} ({string.Join(", ", columns.Select(dialect.QuoteIdentifier))})" +
                  (string.IsNullOrEmpty(insertOverride) ? "" : " " + insertOverride) + " VALUES ";
        var rows = Math.Max(1, batchSize);
        if (dialect.MaxInsertValuesRows is { } valuesLimit) rows = Math.Min(rows, valuesLimit);
        if (dialect.MaxCommandParameters is { } parameterLimit)
        {
            if (_columns > parameterLimit)
                throw new DataTransferException($"The table has {_columns} columns; one row needs more parameters than the target allows ({parameterLimit}).");
            rows = Math.Min(rows, parameterLimit / _columns);
        }
        RowsPerStatement = rows;
    }

    /// <summary>The most rows one command carries.</summary>
    public int RowsPerStatement { get; }

    /// <summary>The rows as commands of at most <see cref="RowsPerStatement"/> rows each, in order.</summary>
    public IReadOnlyList<SqlCommandSpec> Commands(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var commands = new List<SqlCommandSpec>((rows.Count + RowsPerStatement - 1) / RowsPerStatement);
        for (var start = 0; start < rows.Count; start += RowsPerStatement)
            commands.Add(Command(rows, start, Math.Min(RowsPerStatement, rows.Count - start)));
        return commands;
    }

    /// <summary>One row's own command, for retrying a rejected batch row by row.</summary>
    public SqlCommandSpec Command(IReadOnlyList<object?> row) => Command([row], 0, 1);

    private SqlCommandSpec Command(IReadOnlyList<IReadOnlyList<object?>> rows, int start, int count)
    {
        var sql = new StringBuilder(_prefix);
        var parameters = new Dictionary<string, object?>(count * _columns);
        for (var r = 0; r < count; r++)
        {
            var row = rows[start + r];
            sql.Append(r == 0 ? "(" : ", (");
            for (var c = 0; c < _columns; c++)
            {
                var ordinal = parameters.Count;
                if (c > 0) sql.Append(", ");
                sql.Append(_dialect.ParameterPlaceholder(ordinal));
                var value = c < row.Count ? row[c] : null;
                parameters.Add(_dialect.CreateParameterName(ordinal), value is null or DBNull ? _nulls[c] : value);
            }
            sql.Append(')');
        }
        return new SqlCommandSpec(sql.ToString(), parameters);
    }
}
