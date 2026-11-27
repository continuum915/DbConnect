using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Turns the ticked differences of a Data Compare into a <see cref="SyncPlan"/> that makes the target table match the
/// source: an INSERT for a row only in the source, an UPDATE of the differing row, and a DELETE (destructive) for a row
/// only in the target. Each command is parameterized; its preview writes the same values as literals. UPDATE and DELETE
/// must affect exactly one row. Data always runs in one transaction, on every engine.
/// </summary>
public static class DataSyncPlanner
{
    /// <param name="columnTypes">The target's declared column types, for NULLs an engine wants typed (SQL Server binary).</param>
    public static SyncPlan Build(DatabaseSession target, string targetTable, IReadOnlyList<string> primaryKey, IEnumerable<RowDiff> differences,
        IReadOnlyDictionary<string, string>? columnTypes = null)
    {
        var dialect = target.Capabilities;
        var table = DataCompareService.QuoteMultipartIdentifier(dialect, targetTable);
        var steps = new List<SyncStep>();
        foreach (var diff in differences)
        {
            var key = string.IsNullOrEmpty(diff.PrimaryKeyValue) ? "" : $" ({diff.PrimaryKeyValue})";
            var statement = new Statement(dialect, columnTypes);
            switch (diff.Kind)
            {
                case RowDiffKind.OnlyInSource:
                    statement.Append($"INSERT INTO {table} ({string.Join(", ", diff.SourceRow.Keys.Select(dialect.QuoteIdentifier))}) VALUES (");
                    statement.Values(diff.SourceRow, ", ");
                    statement.Append(")");
                    steps.Add(statement.Step($"Row{key}", "Insert", expected: null));
                    break;
                case RowDiffKind.Modified:
                    var changed = diff.SourceRow.Where(kv => !primaryKey.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (changed.Count == 0) continue;
                    statement.Append($"UPDATE {table} SET ");
                    statement.Assignments(changed, ", ");
                    statement.Append(" WHERE ");
                    statement.Match(Key(diff.TargetRow, primaryKey));
                    steps.Add(statement.Step($"Row{key}", "Update", expected: 1));
                    break;
                case RowDiffKind.OnlyInTarget:
                    statement.Append($"DELETE FROM {table} WHERE ");
                    statement.Match(Key(diff.TargetRow, primaryKey));
                    steps.Add(statement.Step($"Row{key}", "Delete", expected: 1) with
                    {
                        Destructive = $"Deletes the target row{key}, which the source does not have."
                    });
                    break;
            }
        }
        var planned = differences.ToList();
        return new SyncPlan(target.Name, target.Server != null ? target.DatabaseLabel : null, target.Connection.EnvironmentTier, steps, Transactional: true)
        {
            DriftCheck = (session, cancellationToken) => DriftAsync(session, dialect, table, primaryKey, planned, cancellationToken)
        };
    }

    /// <summary>
    /// Reads every planned row again by its key, in the apply transaction: a row the comparison saw in the target must
    /// still hold the values it read, and a row it did not see must still be missing. The first that is not is named.
    /// </summary>
    private static async Task<string?> DriftAsync(IDbSession session, DatabaseProviderCapabilities dialect, string table,
        IReadOnlyList<string> primaryKey, IReadOnlyList<RowDiff> planned, System.Threading.CancellationToken cancellationToken)
    {
        foreach (var diff in planned)
        {
            var seen = diff.Kind == RowDiffKind.OnlyInSource ? diff.SourceRow : diff.TargetRow;
            var where = string.Join(" AND ", primaryKey.Select(column =>
                seen.TryGetValue(column, out var value) && value is not (null or DBNull)
                    ? $"{dialect.QuoteIdentifier(column)} = {SqlLiteral.Format(value, dialect)}"
                    : $"{dialect.QuoteIdentifier(column)} IS NULL"));
            var result = await session.ExecuteQueryAsync($"SELECT * FROM {table} WHERE {where}",
                new QueryExecutionOptions { CancellationToken = cancellationToken });
            if (result.HasError) return $"The target could not be checked before applying: {DiagnosticRedactor.Redact(result.ErrorMessage ?? "")}";
            var key = string.IsNullOrEmpty(diff.PrimaryKeyValue) ? "" : $" ({diff.PrimaryKeyValue})";
            string Changed(string what) => $"The target changed since the comparison: row{key} {what}. Nothing was applied; compare again before applying.";
            if (diff.Kind == RowDiffKind.OnlyInSource)
            {
                if (result.ObjectRows.Count > 0) return Changed("has been inserted");
                continue;
            }
            if (result.ObjectRows.Count == 0) return Changed("has been deleted");
            var row = result.ObjectRows[0];
            for (var i = 0; i < result.ColumnInfos.Count; i++)
            {
                if (!diff.TargetRow.TryGetValue(result.ColumnInfos[i].Name, out var before)) continue;
                if (!Same(before, row[i])) return Changed($"has a different {result.ColumnInfos[i].Name}");
            }
        }
        return null;
    }

    private static bool Same(object? before, object? now) => (before, now) switch
    {
        (null or DBNull, null or DBNull) => true,
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        (null or DBNull, _) or (_, null or DBNull) => false,
        _ => Equals(before, now) || string.Equals(Convert.ToString(before, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToString(now, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
    };

    private static IEnumerable<KeyValuePair<string, object?>> Key(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> primaryKey) =>
        primaryKey.Select(column => new KeyValuePair<string, object?>(column, row.TryGetValue(column, out var value) ? value : null));

    /// <summary>One statement written twice from the same values: with parameters to run, with literals to show.</summary>
    private sealed class Statement(DatabaseProviderCapabilities dialect, IReadOnlyDictionary<string, string>? columnTypes)
    {
        private readonly System.Text.StringBuilder _sql = new();
        private readonly System.Text.StringBuilder _preview = new();
        private readonly Dictionary<string, object?> _parameters = new();

        public void Append(string text)
        {
            _sql.Append(text);
            _preview.Append(text);
        }

        private void Value(string column, object? value)
        {
            var ordinal = _parameters.Count;
            _sql.Append(dialect.ParameterPlaceholder(ordinal));
            var typedNull = value is null or DBNull && columnTypes != null && columnTypes.TryGetValue(column, out var type)
                ? dialect.TypedNull?.Invoke(type) : null;
            _parameters.Add(dialect.CreateParameterName(ordinal), value is null or DBNull ? typedNull : value);
            _preview.Append(SqlLiteral.Format(value is DBNull ? null : value, dialect));
        }

        public void Values(IEnumerable<KeyValuePair<string, object?>> row, string separator)
        {
            var first = true;
            foreach (var (column, value) in row)
            {
                if (!first) Append(separator);
                first = false;
                Value(column, value);
            }
        }

        public void Assignments(IEnumerable<KeyValuePair<string, object?>> row, string separator)
        {
            var first = true;
            foreach (var (column, value) in row)
            {
                if (!first) Append(separator);
                first = false;
                Append(dialect.QuoteIdentifier(column) + " = ");
                Value(column, value);
            }
        }

        /// <summary>Equality on each column; a NULL is matched with IS NULL, which no parameter can express.</summary>
        public void Match(IEnumerable<KeyValuePair<string, object?>> row)
        {
            var first = true;
            foreach (var (column, value) in row)
            {
                if (!first) Append(" AND ");
                first = false;
                if (value is null or DBNull)
                {
                    Append(dialect.QuoteIdentifier(column) + " IS NULL");
                    continue;
                }
                Append(dialect.QuoteIdentifier(column) + " = ");
                Value(column, value);
            }
        }

        public SyncStep Step(string target, string action, int? expected) =>
            new(target, action, new SqlCommandSpec(_sql.ToString(), _parameters, expected), _preview.ToString());
    }
}
