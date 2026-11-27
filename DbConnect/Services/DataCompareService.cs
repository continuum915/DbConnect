using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

public interface IDataCompareService
{
    IAsyncEnumerable<DataCompareChunk> CompareAsync(
        DataCompareRequest request,
        IProgress<DataSyncProgress>? progress = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> GenerateSyncScriptAsync(
        IEnumerable<RowDiff> differences,
        string targetTable,
        List<string> primaryKeyColumns,
        DatabaseProviderCapabilities targetDialect,
        ConflictResolutionPolicy policy = ConflictResolutionPolicy.SourceWins,
        int batchSize = 1000);
}

public class DataCompareService : IDataCompareService
{
    private const int CompareChunkSize = 5_000;

    public async Task<DataDiffResult> CompareAsync(
        DatabaseSession source,
        string sourceTable,
        DatabaseSession target,
        string targetTable,
        int rowLimit = 10000)
    {
        var result = new DataDiffResult
        {
            SourceTable = sourceTable,
            TargetTable = targetTable
        };

        try
        {
            var primaryKeyColumns = await TablePrimaryKeys.ResolveAsync(source, sourceTable);
            if (primaryKeyColumns.Count == 0)
            {
                result.ErrorMessage = "No primary key found on source table.";
                return result;
            }

            result.PrimaryKeyColumns = primaryKeyColumns;
            var request = new DataCompareRequest
            {
                SourceSession = source,
                TargetSession = target,
                SourceTable = sourceTable,
                TargetTable = targetTable,
                PrimaryKeyColumns = primaryKeyColumns,
                MaxRows = rowLimit,
                ConflictPolicy = ConflictResolutionPolicy.SourceWins
            };

            await foreach (var chunk in CompareAsync(request, ct: CancellationToken.None))
            {
                foreach (var diff in chunk.Differences)
                    result.Rows.Add(ToLegacyRow(diff, primaryKeyColumns));

                result.TotalCompared = (int)Math.Min(int.MaxValue, chunk.Progress.RowsCompared);
                result.IsTruncated = chunk.Progress.IsTruncated;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async IAsyncEnumerable<DataCompareChunk> CompareAsync(
        DataCompareRequest request,
        IProgress<DataSyncProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceSession);
        ArgumentNullException.ThrowIfNull(request.TargetSession);

        if (string.IsNullOrWhiteSpace(request.SourceTable))
            throw new ArgumentException("Source table is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetTable))
            throw new ArgumentException("Target table is required.", nameof(request));

        var primaryKeyColumns = request.PrimaryKeyColumns.Count > 0
            ? request.PrimaryKeyColumns
            : await TablePrimaryKeys.ResolveAsync(request.SourceSession, request.SourceTable);

        if (primaryKeyColumns.Count == 0)
            throw new InvalidOperationException("No primary key found on source table.");

        var whereClause = string.IsNullOrWhiteSpace(request.WhereFilter)
            ? string.Empty
            : $" WHERE {request.WhereFilter}";

        var sourceOrderBy = string.Join(", ", primaryKeyColumns.Select(request.SourceSession.Capabilities.QuoteIdentifier));
        var targetOrderBy = string.Join(", ", primaryKeyColumns.Select(request.TargetSession.Capabilities.QuoteIdentifier));
        var sourceQuery = $"SELECT * FROM {QuoteMultipartIdentifier(request.SourceSession.Capabilities, request.SourceTable)}{whereClause} ORDER BY {sourceOrderBy}";
        var targetQuery = $"SELECT * FROM {QuoteMultipartIdentifier(request.TargetSession.Capabilities, request.TargetTable)}{whereClause} ORDER BY {targetOrderBy}";

        await using var sourceScope = await OpenReaderScopeAsync(request.SourceSession, sourceQuery, ct);
        await using var targetScope = await OpenReaderScopeAsync(request.TargetSession, targetQuery, ct);

        var diffBuffer = new List<RowDiff>(CompareChunkSize);
        var rowsCompared = 0L;
        var differencesFound = 0L;
        var maxRows = request.MaxRows > 0 ? request.MaxRows : int.MaxValue;
        var hasSource = await sourceScope.Reader.ReadAsync(ct);
        var hasTarget = await targetScope.Reader.ReadAsync(ct);
        string? currentPrimaryKey = null;
        var truncated = false;

        while (hasSource || hasTarget)
        {
            ct.ThrowIfCancellationRequested();

            if (rowsCompared >= maxRows)
            {
                truncated = hasSource || hasTarget;
                break;
            }

            var diff = BuildNextDiff(sourceScope, targetScope, primaryKeyColumns, hasSource, hasTarget);
            currentPrimaryKey = diff.PrimaryKeyValue;
            diffBuffer.Add(diff);
            if (diff.Kind != RowDiffKind.Equal)
                differencesFound++;

            rowsCompared++;

            if (diff.Kind == RowDiffKind.OnlyInSource)
            {
                hasSource = await sourceScope.Reader.ReadAsync(ct);
            }
            else if (diff.Kind == RowDiffKind.OnlyInTarget)
            {
                hasTarget = await targetScope.Reader.ReadAsync(ct);
            }
            else
            {
                hasSource = await sourceScope.Reader.ReadAsync(ct);
                hasTarget = await targetScope.Reader.ReadAsync(ct);
            }

            if (diffBuffer.Count >= CompareChunkSize)
            {
                var chunkProgress = CreateProgress(rowsCompared, differencesFound, false, currentPrimaryKey);
                progress?.Report(chunkProgress);
                yield return new DataCompareChunk
                {
                    Differences = new List<RowDiff>(diffBuffer),
                    Progress = chunkProgress,
                    IsLast = false
                };
                diffBuffer.Clear();
            }
        }

        var finalProgress = CreateProgress(rowsCompared, differencesFound, truncated, currentPrimaryKey);
        progress?.Report(finalProgress);
        yield return new DataCompareChunk
        {
            Differences = new List<RowDiff>(diffBuffer),
            Progress = finalProgress,
            IsLast = true
        };
    }

    public async IAsyncEnumerable<string> GenerateSyncScriptAsync(
        IEnumerable<RowDiff> differences,
        string targetTable,
        List<string> primaryKeyColumns,
        DatabaseProviderCapabilities targetDialect,
        ConflictResolutionPolicy policy = ConflictResolutionPolicy.SourceWins,
        int batchSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(differences);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTable);
        ArgumentNullException.ThrowIfNull(primaryKeyColumns);

        var effectiveBatchSize = batchSize > 0 ? batchSize : 1000;
        var statementsInBatch = 0;
        yield return targetDialect.BeginTransactionStatement;

        foreach (var diff in differences)
        {
            var sql = BuildSyncStatement(diff, targetTable, primaryKeyColumns, policy, targetDialect);
            if (string.IsNullOrWhiteSpace(sql))
                continue;

            yield return sql;
            statementsInBatch++;

            if (statementsInBatch % effectiveBatchSize == 0)
            {
                yield return "COMMIT;";
                yield return targetDialect.BeginTransactionStatement;
            }
        }

        yield return "COMMIT;";
        await Task.CompletedTask;
    }

    private static DataSyncProgress CreateProgress(long rowsCompared, long differencesFound, bool isTruncated, string? currentPrimaryKey) =>
        new()
        {
            RowsCompared = rowsCompared,
            DifferencesFound = differencesFound,
            IsTruncated = isTruncated,
            CurrentPrimaryKey = currentPrimaryKey
        };

    private static RowDiff BuildNextDiff(
        ReaderScope sourceScope,
        ReaderScope targetScope,
        IReadOnlyList<string> primaryKeyColumns,
        bool hasSource,
        bool hasTarget)
    {
        if (!hasTarget)
        {
            var sourceRow = ReadCurrentRow(sourceScope);
            return new RowDiff
            {
                Kind = RowDiffKind.OnlyInSource,
                SourceRow = sourceRow,
                PrimaryKeyValue = BuildPrimaryKeyValue(sourceRow, primaryKeyColumns)
            };
        }

        if (!hasSource)
        {
            var targetRow = ReadCurrentRow(targetScope);
            return new RowDiff
            {
                Kind = RowDiffKind.OnlyInTarget,
                TargetRow = targetRow,
                PrimaryKeyValue = BuildPrimaryKeyValue(targetRow, primaryKeyColumns)
            };
        }

        var keyComparison = ComparePrimaryKeys(sourceScope, targetScope, primaryKeyColumns);
        if (keyComparison < 0)
        {
            var sourceRow = ReadCurrentRow(sourceScope);
            return new RowDiff
            {
                Kind = RowDiffKind.OnlyInSource,
                SourceRow = sourceRow,
                PrimaryKeyValue = BuildPrimaryKeyValue(sourceRow, primaryKeyColumns)
            };
        }

        if (keyComparison > 0)
        {
            var targetRow = ReadCurrentRow(targetScope);
            return new RowDiff
            {
                Kind = RowDiffKind.OnlyInTarget,
                TargetRow = targetRow,
                PrimaryKeyValue = BuildPrimaryKeyValue(targetRow, primaryKeyColumns)
            };
        }

        var sourceEqualRow = ReadCurrentRow(sourceScope);
        var targetEqualRow = ReadCurrentRow(targetScope);
        return new RowDiff
        {
            Kind = RowsEqual(sourceEqualRow, targetEqualRow, primaryKeyColumns) ? RowDiffKind.Equal : RowDiffKind.Modified,
            SourceRow = sourceEqualRow,
            TargetRow = targetEqualRow,
            PrimaryKeyValue = BuildPrimaryKeyValue(sourceEqualRow, primaryKeyColumns)
        };
    }

    private static DataDiffRow ToLegacyRow(RowDiff diff, IReadOnlyList<string> primaryKeyColumns) =>
        diff.Kind switch
        {
            RowDiffKind.OnlyInSource => new DataDiffRow
            {
                Status = RowDiffStatus.OnlyInSource,
                SourceValues = ToStringRow(diff.SourceRow)
            },
            RowDiffKind.OnlyInTarget => new DataDiffRow
            {
                Status = RowDiffStatus.OnlyInTarget,
                TargetValues = ToStringRow(diff.TargetRow)
            },
            RowDiffKind.Modified => new DataDiffRow
            {
                Status = RowDiffStatus.Modified,
                SourceValues = ToStringRow(diff.SourceRow),
                TargetValues = ToStringRow(diff.TargetRow),
                ChangedColumns = FindChangedColumns(diff.SourceRow, diff.TargetRow, primaryKeyColumns)
            },
            _ => new DataDiffRow
            {
                Status = RowDiffStatus.Identical,
                SourceValues = ToStringRow(diff.SourceRow),
                TargetValues = ToStringRow(diff.TargetRow)
            }
        };

    private static Dictionary<string, string?> ToStringRow(Dictionary<string, object?> row)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in row)
            result[key] = FormatDisplayValue(value);

        return result;
    }

    private static List<string> FindChangedColumns(
        Dictionary<string, object?> sourceRow,
        Dictionary<string, object?> targetRow,
        IReadOnlyList<string> primaryKeyColumns)
    {
        var changed = new List<string>();
        var allColumns = sourceRow.Keys.Union(targetRow.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var column in allColumns)
        {
            if (primaryKeyColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                continue;

            sourceRow.TryGetValue(column, out var sourceValue);
            targetRow.TryGetValue(column, out var targetValue);
            if (!ValuesEqual(sourceValue, targetValue))
                changed.Add(column);
        }

        return changed;
    }

    private static bool RowsEqual(
        Dictionary<string, object?> sourceRow,
        Dictionary<string, object?> targetRow,
        IReadOnlyList<string> primaryKeyColumns)
    {
        var allColumns = sourceRow.Keys.Union(targetRow.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var column in allColumns)
        {
            if (primaryKeyColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                continue;

            sourceRow.TryGetValue(column, out var sourceValue);
            targetRow.TryGetValue(column, out var targetValue);
            if (!ValuesEqual(sourceValue, targetValue))
                return false;
        }

        return true;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is DBNull)
            left = null;
        if (right is DBNull)
            right = null;

        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;

        if (TryGetBytes(left, out var leftBytes) && TryGetBytes(right, out var rightBytes))
            return leftBytes.SequenceEqual(rightBytes);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (IsFloatingPoint(left) || IsFloatingPoint(right))
                return Math.Abs(Convert.ToDouble(left, CultureInfo.InvariantCulture) - Convert.ToDouble(right, CultureInfo.InvariantCulture)) < 1e-9;

            return decimal.Compare(
                Convert.ToDecimal(left, CultureInfo.InvariantCulture),
                Convert.ToDecimal(right, CultureInfo.InvariantCulture)) == 0;
        }

        if (left is string leftString && right is string rightString)
            return string.Equals(leftString, rightString, StringComparison.Ordinal);

        if (left is DateTime leftDateTime && right is DateTime rightDateTime)
            return leftDateTime.Equals(rightDateTime);

        if (left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset)
            return leftOffset.EqualsExact(rightOffset);

        if (left.GetType() == right.GetType())
            return left.Equals(right);

        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool TryGetBytes(object value, out byte[] bytes)
    {
        switch (value)
        {
            case byte[] array:
                bytes = array;
                return true;
            case ReadOnlyMemory<byte> readOnlyMemory:
                bytes = readOnlyMemory.ToArray();
                return true;
            case Memory<byte> memory:
                bytes = memory.ToArray();
                return true;
            default:
                bytes = Array.Empty<byte>();
                return false;
        }
    }

    private static bool IsNumeric(object value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => true,
        _ => false
    };

    private static bool IsFloatingPoint(object value) => value is float or double;

    private static int ComparePrimaryKeys(ReaderScope sourceScope, ReaderScope targetScope, IReadOnlyList<string> primaryKeyColumns)
    {
        foreach (var column in primaryKeyColumns)
        {
            var sourceValue = GetReaderValue(sourceScope, column);
            var targetValue = GetReaderValue(targetScope, column);
            var comparison = CompareKeyValues(sourceValue, targetValue);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareKeyValues(object? left, object? right)
    {
        if (left is DBNull)
            left = null;
        if (right is DBNull)
            right = null;

        if (left is null && right is null)
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (IsFloatingPoint(left) || IsFloatingPoint(right))
                return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

            return Convert.ToDecimal(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }

        if (left is string leftString && right is string rightString)
            return StringComparer.Ordinal.Compare(leftString, rightString);

        if (left is IComparable comparableLeft && right.GetType() == left.GetType())
            return comparableLeft.CompareTo(right);

        return StringComparer.Ordinal.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture));
    }

    private static object? GetReaderValue(ReaderScope scope, string column) =>
        DbReaderResults.ReadValue(scope.Reader, scope.Ordinals[column]);

    private static Dictionary<string, object?> ReadCurrentRow(ReaderScope scope)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < scope.Reader.FieldCount; i++)
            row[scope.Reader.GetName(i)] = DbReaderResults.ReadValue(scope.Reader, i);

        return row;
    }

    private static string BuildPrimaryKeyValue(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> primaryKeyColumns) =>
        string.Join(" | ", primaryKeyColumns.Select(column =>
        {
            row.TryGetValue(column, out var value);
            return FormatDisplayValue(value) ?? "NULL";
        }));

    private static string FormatDisplayValue(object? value) => value switch
    {
        null => null!,
        byte[] bytes => Convert.ToHexString(bytes),
        ReadOnlyMemory<byte> readOnlyMemory => Convert.ToHexString(readOnlyMemory.ToArray()),
        Memory<byte> memory => Convert.ToHexString(memory.ToArray()),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    internal static string QuoteMultipartIdentifier(DatabaseProviderCapabilities dialect, string identifier) =>
        string.Join(".", identifier.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dialect.QuoteIdentifier));


    private static async Task<ReaderScope> OpenReaderScopeAsync(DatabaseSession session, string query, CancellationToken ct)
    {
        DbConnection connection = session.Provider switch
        {
            SqliteProvider => new SqliteConnection(session.ConnectionString),
            PostgreSqlProvider => new NpgsqlConnection(session.ConnectionString),
            SqlServerProvider => new SqlConnection(session.ConnectionString),
            MySqlProvider => new MySqlConnection(new MySqlConnectionStringBuilder(session.ConnectionString)
            {
                AllowUserVariables = true
            }.ConnectionString),
            DuckDbProvider => new DuckDBConnection(NormalizeDuckDbConnectionString(session.ConnectionString)),
            _ => throw new NotSupportedException($"Streaming data compare is not supported for provider '{session.ProviderName}'.")
        };

        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = query;
        var reader = await command.ExecuteReaderAsync(ct); // Not SequentialAccess: keys are read before the whole row.
        return new ReaderScope(connection, command, reader);
    }

    private static string NormalizeDuckDbConnectionString(string connectionString)
    {
        var trimmed = connectionString.Trim();
        return trimmed.Contains('=') ? trimmed : $"Data Source={trimmed}";
    }

    private static string BuildSyncStatement(
        RowDiff diff,
        string targetTable,
        IReadOnlyList<string> primaryKeyColumns,
        ConflictResolutionPolicy policy,
        DatabaseProviderCapabilities dialect)
    {
        return policy switch
        {
            ConflictResolutionPolicy.SourceWins => diff.Kind switch
            {
                RowDiffKind.OnlyInSource => BuildInsert(diff.SourceRow, targetTable, dialect),
                RowDiffKind.OnlyInTarget => BuildDelete(diff.TargetRow, targetTable, primaryKeyColumns, dialect),
                RowDiffKind.Modified => BuildUpdate(diff.SourceRow, targetTable, primaryKeyColumns, dialect),
                _ => string.Empty
            },
            ConflictResolutionPolicy.TargetWins => string.Empty,
            ConflictResolutionPolicy.ManualReview => $"-- Manual review required for {diff.Kind} row with PK {EscapeComment(diff.PrimaryKeyValue)}",
            _ => string.Empty
        };
    }

    private static string BuildInsert(IReadOnlyDictionary<string, object?> row, string targetTable, DatabaseProviderCapabilities dialect)
    {
        var columns = row.Keys.Select(dialect.QuoteIdentifier).ToList();
        var values = row.Values.Select(value => SqlLiteral.Format(value, dialect)).ToList();
        return $"INSERT INTO {QuoteMultipartIdentifier(dialect, targetTable)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
    }

    private static string BuildUpdate(
        IReadOnlyDictionary<string, object?> row, string targetTable, IReadOnlyList<string> primaryKeyColumns, DatabaseProviderCapabilities dialect)
    {
        var assignments = row
            .Where(kv => !primaryKeyColumns.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => $"{dialect.QuoteIdentifier(kv.Key)} = {SqlLiteral.Format(kv.Value, dialect)}")
            .ToList();

        if (assignments.Count == 0)
            return string.Empty;

        var whereClause = BuildPrimaryKeyWhereClause(row, primaryKeyColumns, dialect);
        return $"UPDATE {QuoteMultipartIdentifier(dialect, targetTable)} SET {string.Join(", ", assignments)} WHERE {whereClause};";
    }

    private static string BuildDelete(
        IReadOnlyDictionary<string, object?> row, string targetTable, IReadOnlyList<string> primaryKeyColumns, DatabaseProviderCapabilities dialect)
    {
        var whereClause = BuildPrimaryKeyWhereClause(row, primaryKeyColumns, dialect);
        return $"DELETE FROM {QuoteMultipartIdentifier(dialect, targetTable)} WHERE {whereClause};";
    }

    private static string BuildPrimaryKeyWhereClause(
        IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> primaryKeyColumns, DatabaseProviderCapabilities dialect)
    {
        var predicates = new List<string>(primaryKeyColumns.Count);
        foreach (var column in primaryKeyColumns)
        {
            row.TryGetValue(column, out var value);
            predicates.Add(value is null
                ? $"{dialect.QuoteIdentifier(column)} IS NULL"
                : $"{dialect.QuoteIdentifier(column)} = {SqlLiteral.Format(value, dialect)}");
        }

        return string.Join(" AND ", predicates);
    }

    private static string EscapeComment(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private sealed class ReaderScope : IAsyncDisposable
    {
        public ReaderScope(DbConnection connection, DbCommand command, DbDataReader reader)
        {
            Connection = connection;
            Command = command;
            Reader = reader;
            Ordinals = Enumerable.Range(0, reader.FieldCount)
                .ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);
        }

        public DbConnection Connection { get; }
        public DbCommand Command { get; }
        public DbDataReader Reader { get; }
        public Dictionary<string, int> Ordinals { get; }

        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync();
            await Command.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
