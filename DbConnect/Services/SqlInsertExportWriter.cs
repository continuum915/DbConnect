using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A value with no SQL literal form; the export fails rather than writing a script that changes data.</summary>
internal sealed class SqlInsertExportException(string message) : Exception(message);

/// <summary>
/// Streams rows as INSERT statements for the source dialect: one statement per row, or multi-row VALUES lists of
/// <see cref="ExportOptions.RowsPerStatement"/> rows, never more than the dialect's VALUES-list limit. Only the
/// current statement's rows are held.
/// </summary>
internal static class SqlInsertExportWriter
{
    public static async Task WriteAsync(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        ExportOptions options, Action rowWritten, CancellationToken cancellationToken)
    {
        var dialect = options.SqlDialect;
        var table = string.IsNullOrWhiteSpace(options.SqlTableName) ? SqlInsertTarget.Fallback(dialect) : options.SqlTableName.Trim();
        var prefix = $"INSERT INTO {table}" +
            (options.IncludeColumnList ? $" ({string.Join(", ", columns.Select(c => SqlInsertTarget.Quote(c.Name, dialect)))})" : "") +
            (string.IsNullOrEmpty(options.SqlInsertOverride) ? "" : " " + options.SqlInsertOverride) + " VALUES";
        var perStatement = Math.Max(1, options.RowsPerStatement);
        if (dialect?.MaxInsertValuesRows is { } limit) perStatement = Math.Min(perStatement, limit);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
        if (options.IncludeCreateTable && options.CreateTableScript is { } script)
        {
            var created = await script(cancellationToken);
            if (created.Sql is null)
                throw new SqlInsertExportException($"CREATE TABLE could not be scripted: {created.Error}");
            await writer.WriteAsync(created.Sql.ReplaceLineEndings("\n").TrimEnd().AsMemory(), cancellationToken);
            await writer.WriteAsync("\n\n".AsMemory(), cancellationToken);
        }
        var pending = new List<string>(perStatement);
        await foreach (var row in source.ReadRowsAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Add(Tuple(row, columns, dialect));
            if (pending.Count == perStatement) await FlushAsync(writer, prefix, pending, cancellationToken);
            rowWritten();
        }
        if (pending.Count > 0) await FlushAsync(writer, prefix, pending, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string Tuple(IReadOnlyList<object?> row, IReadOnlyList<ExportColumn> columns, DatabaseProviderCapabilities? dialect)
    {
        var values = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var value = i < row.Count ? row[i] : null;
            if (value is DateTime instant && IsTimeZoneAware(columns[i].ProviderTypeName))
                value = instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc);
            if (!SqlLiteral.TryFormat(value, dialect, out var literal))
                throw new SqlInsertExportException($"Column \"{columns[i].Name}\" holds a {value!.GetType().Name} value with no SQL literal form.");
            values[i] = literal;
        }
        return $"({string.Join(", ", values)})";
    }

    /// <summary>
    /// Time-zone-aware timestamps are read as UTC instants, though some drivers (DuckDB) leave the kind
    /// unspecified; written without an offset they would be re-read in the session's time zone.
    /// </summary>
    private static bool IsTimeZoneAware(string? typeName) =>
        typeName != null && (typeName.Equals("timestamptz", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("timestamp with time zone", StringComparison.OrdinalIgnoreCase));

    private static async Task FlushAsync(StreamWriter writer, string prefix, List<string> tuples, CancellationToken token)
    {
        var statement = tuples.Count == 1
            ? $"{prefix} {tuples[0]};\n"
            : $"{prefix}\n{string.Join(",\n", tuples)};\n";
        await writer.WriteAsync(statement.AsMemory(), token);
        tuples.Clear();
    }
}
