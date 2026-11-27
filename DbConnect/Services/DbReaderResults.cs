using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Reads a command's outcome from its data reader instead of guessing from the SQL text:
/// a result set exists when the reader reports fields, and rows-affected is reported when it does not.
/// </summary>
public static class DbReaderResults
{
    /// <summary>
    /// Fills <paramref name="result"/> from the first result set that has fields. The reader is
    /// then run to its end (unless the row cap stops it early), so a statement that fails after its rows still surfaces its error and
    /// rows-affected is complete. Later result sets are not surfaced yet.
    /// </summary>
    public static async Task ReadAsync(DbDataReader reader, QueryResult result, QueryExecutionOptions? options)
    {
        var cancellationToken = options?.CancellationToken ?? default;
        var hasResultSet = false;
        var recordsAffected = -1;

        do
        {
            // Some drivers accumulate the count across results and others report it per result and
            // reset once the reader runs off the end, so the largest value seen before advancing is kept.
            recordsAffected = Math.Max(recordsAffected, reader.RecordsAffected);

            if (hasResultSet || reader.FieldCount == 0)
                continue;

            hasResultSet = true;
            var ordinals = AddVisibleColumns(reader, result.ColumnInfos, options?.IncludeColumnSchema ?? true);

            while (await reader.ReadAsync(cancellationToken))
            {
                if (options?.MaxResultRows > 0 && result.ObjectRows.Count >= options.MaxResultRows)
                {
                    result.IsTruncated = true;
                    result.RowCap = options.MaxResultRows;
                    if (options.Continuation is { IsArmed: true } continuation)
                        await continuation.ServeAsync(reader, ordinals, result, [result], null);
                    return;
                }

                result.ObjectRows.Add(ReadRowValues(reader, ordinals));
            }
        } while (await reader.NextResultAsync(cancellationToken));

        // Drivers count returned rows as "affected" for some statements, so only a statement
        // that returned no result set reports a count. A negative count means the driver has none to give.
        if (!hasResultSet)
            result.RecordsAffected = Math.Max(recordsAffected, reader.RecordsAffected);
    }

    /// <summary>
    /// Reads every result set the reader produces, one <see cref="QueryResult"/> per result set
    /// that has fields. A statement with no such result set (pure DDL/DML) yields a single result
    /// carrying its rows-affected count, matching <see cref="ReadAsync"/>'s no-result-set behavior.
    /// Hitting the row cap stops draining immediately, same as <see cref="ReadAsync"/>: the reader
    /// is not run to completion, so any result sets after the truncated one are not surfaced.
    /// </summary>
    public static async Task<List<QueryResult>> ReadAllAsync(DbDataReader reader, QueryExecutionOptions? options)
    {
        var results = new List<QueryResult>();
        await ReadAllAsync(reader, options, results);
        return results;
    }

    /// <summary>Adds to <paramref name="results"/> as it reads, so a caller keeps what came before a failure.</summary>
    public static async Task ReadAllAsync(DbDataReader reader, QueryExecutionOptions? options, List<QueryResult> results)
    {
        var cancellationToken = options?.CancellationToken ?? default;
        var recordsAffected = -1;

        do
        {
            recordsAffected = Math.Max(recordsAffected, reader.RecordsAffected);

            if (reader.FieldCount == 0)
                continue;

            var result = new QueryResult();
            var ordinals = AddVisibleColumns(reader, result.ColumnInfos, options?.IncludeColumnSchema ?? true);

            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (options?.MaxResultRows > 0 && result.ObjectRows.Count >= options.MaxResultRows)
                {
                    result.IsTruncated = true;
                    result.RowCap = options.MaxResultRows;
                    truncated = true;
                    break;
                }

                result.ObjectRows.Add(ReadRowValues(reader, ordinals));
            }

            results.Add(result);
            if (truncated && options?.Continuation is { IsArmed: true } continuation)
            {
                // Later result sets are read, to the plain cap, once the parked one is exhausted.
                var rest = options with { Continuation = null };
                await continuation.ServeAsync(reader, ordinals, result, results.ToArray(), async () =>
                {
                    var later = new List<QueryResult>();
                    if (await reader.NextResultAsync())
                        await ReadAllAsync(reader, rest with { CancellationToken = default }, later);
                    return later.Where(r => r.HasResultSet).ToList();
                });
                break;
            }
            if (truncated)
                break;
        } while (await reader.NextResultAsync(cancellationToken));

        if (results.Count == 0)
            results.Add(new QueryResult { RecordsAffected = Math.Max(recordsAffected, reader.RecordsAffected) });
    }

    /// <summary>Reads the current row's values at the given reader ordinals, in result order.</summary>
    internal static object?[] ReadRowValues(DbDataReader reader, int[] ordinals)
    {
        var values = new object?[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
            values[i] = ReadValue(reader, ordinals[i]);

        return values;
    }

    /// <summary>
    /// The value at <paramref name="ordinal"/>, safe to keep after the reader moves on: DBNull becomes
    /// null, and a stream (DuckDB's BLOBs, over memory only valid while the reader is open) is copied
    /// into a byte array.
    /// </summary>
    public static object? ReadValue(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DBNull => null,
        Stream stream => ReadAllBytes(stream),
        var value => value
    };

    private static byte[] ReadAllBytes(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Adds one <see cref="ColumnInfo"/> per visible field to <paramref name="infos"/>, by ordinal, so
    /// <c>SELECT a.id, b.id</c> comes back as two distinct entries named "id" rather than one. Source
    /// table/column, nullability and key-ness come from the reader's column schema when the driver exposes
    /// one; providers that don't still get name, ordinal, provider type name and CLR type from the reader.
    /// Hidden fields (key columns SQL Server's browse mode appends) are left out; the returned reader
    /// ordinals, one per added column, are where each row's values are read from.
    /// </summary>
    private static int[] AddVisibleColumns(DbDataReader reader, List<ColumnInfo> infos, bool columnSchema)
    {
        ReadOnlyCollection<DbColumn>? schema = null;
        try
        {
            if (columnSchema && reader.CanGetColumnSchema())
                schema = reader.GetColumnSchema();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Column schema is optional metadata. Some drivers implement CanGetColumnSchema but still throw
            // for certain readers (e.g. a schema-only or already-advanced result), and Npgsql's KeyInfo lookup
            // throws for a type created after the connection loaded its types; the per-field fallback covers it.
        }

        var ordinals = new List<int>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var column = schema != null && i < schema.Count ? schema[i] : null;
            if (column?.IsHidden == true)
                continue;

            var name = reader.GetName(i);
            var sourceTable = NullIfEmpty(column?.BaseTableName);
            // A source column means nothing without its table, so an expression reports neither. SQL Server
            // sends a base column name only when it differs from the result name, so a column from a known
            // table with no base name is the unaliased column itself.
            var sourceColumn = sourceTable is null ? null : NullIfEmpty(column?.BaseColumnName) ?? name;
            infos.Add(new ColumnInfo
            {
                Name = name,
                Ordinal = ordinals.Count,
                ProviderTypeName = column?.DataTypeName ?? SafeDataTypeName(reader, i),
                ClrType = column?.DataType ?? SafeFieldType(reader, i),
                SourceSchema = sourceTable is null ? null : NullIfEmpty(column?.BaseSchemaName),
                SourceTable = sourceTable,
                SourceColumn = sourceColumn,
                IsNullable = column?.AllowDBNull,
                IsKey = column?.IsKey
            });
            ordinals.Add(i);
        }

        return ordinals.ToArray();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? SafeDataTypeName(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static Type? SafeFieldType(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldType(ordinal);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
