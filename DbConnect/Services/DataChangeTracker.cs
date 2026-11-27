using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// RAIN-203 — Turns dirty <see cref="EditableRow"/>s into SQL: parameterized commands for
/// commit, and literal statements for the commit preview. It holds no row state of its own —
/// edits, originals and revert live on <see cref="EditableRow"/> — and executes nothing: the
/// caller runs the commands through <c>IDbSession.ExecuteTransactionAsync</c>, which rolls back on
/// any failure or on an UPDATE/DELETE whose affected-row count isn't the expected 1.
///
/// Row identity for UPDATE/DELETE WHERE clauses:
///   - If primary key columns are supplied, they are used.
///   - Otherwise ALL original column values form a composite key. The table tab only allows
///     this when the user opts in to the all-column identity fallback.
///
/// RAIN-P2-12 — An UPDATE's WHERE also checks that each edited cell still holds its original value,
/// so a row someone else changed in the same cells since it loaded affects 0 rows and surfaces as a
/// concurrency conflict rather than being silently overwritten. Changes to other columns still
/// merge. Columns the database can't compare with <c>=</c> are left out of the check, and a row the
/// user chose to overwrite (<see cref="EditableRow.OverwritesConcurrentChanges"/>) skips it.
/// </summary>
public class DataChangeTracker
{
    public DatabaseObjectIdentity? Object { get; init; }
    private readonly string _tableName;
    private readonly string? _schemaName;
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<string> _primaryKeyColumns;
    private readonly DatabaseProviderCapabilities? _capabilities;
    private readonly HashSet<string> _nonComparableColumns;

    /// <param name="tableName">Unquoted table name.</param>
    /// <param name="columns">Ordered column names as returned by the SELECT.</param>
    /// <param name="primaryKeyColumns">Known PKs, or empty to use all-columns composite key.</param>
    /// <param name="nonComparableColumns">Columns whose type the database can't compare with <c>=</c>
    /// (e.g. PostgreSQL <c>json</c>), left out of an UPDATE's concurrency check.</param>
    /// <param name="schemaName">RAIN-P2-16 — Unquoted schema of the table, quoted apart from it; null for none.</param>
    public DataChangeTracker(
        string tableName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        DatabaseProviderCapabilities? capabilities = null,
        IEnumerable<string>? nonComparableColumns = null,
        string? schemaName = null)
    {
        _tableName = tableName;
        _schemaName = schemaName;
        _columns = columns;
        _primaryKeyColumns = primaryKeyColumns != null && primaryKeyColumns.Count > 0
            ? primaryKeyColumns
            : columns; // fallback: all columns as composite key
        _capabilities = capabilities;
        _nonComparableColumns = new HashSet<string>(nonComparableColumns ?? [], StringComparer.Ordinal);
    }

    // ── SQL Preview ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a human-readable SQL preview for all dirty rows.
    /// Used in the Commit confirmation dialog (RAIN-204).
    /// </summary>
    public string GenerateSqlPreview(IEnumerable<EditableRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows.Where(r => r.IsDirty))
        {
            sb.AppendLine(BuildStatement(row));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Per-statement builders ────────────────────────────────────────────────

    public string BuildStatement(EditableRow row) => row.State switch
    {
        EditState.Modified => BuildUpdate(row),
        EditState.Inserted => BuildInsert(row),
        EditState.Deleted => BuildDelete(row),
        _ => string.Empty
    };

    public SqlCommandSpec? BuildCommand(EditableRow row) => row.State switch
    {
        EditState.Modified => BuildUpdateCommand(row),
        EditState.Inserted => BuildInsertCommand(row),
        EditState.Deleted => BuildDeleteCommand(row),
        _ => null
    };

    /// <summary>
    /// RAIN-P2-10 — Only columns the user explicitly touched go in the INSERT; identity/generated/default
    /// columns the user never set are left out entirely so the server applies its own value for them.
    /// </summary>
    private List<string> InsertColumns(EditableRow row) =>
        _columns.Where(c => row.EditedColumns.Contains(c) && row.Values.GetValueOrDefault(c) is not ColumnDefault).ToList();

    private string BuildInsert(EditableRow row)
    {
        var insertColumns = InsertColumns(row);
        if (insertColumns.Count == 0)
            return BuildDefaultValuesInsert();

        var cols = string.Join(", ", insertColumns.Select(Quote));
        var vals = string.Join(", ", insertColumns.Select(c => FormatValue(row.Values.GetValueOrDefault(c))));
        return $"INSERT INTO {QuotedTable} ({cols}) VALUES ({vals});";
    }

    private string BuildDefaultValuesInsert() =>
        _capabilities?.SupportsInsertDefaultValues == false
            ? $"INSERT INTO {QuotedTable} () VALUES ();"
            : $"INSERT INTO {QuotedTable} DEFAULT VALUES;";

    /// <summary>An UPDATE sets only the columns whose value actually changed, in table order.</summary>
    private List<string> ChangedColumns(EditableRow row) => _columns.Where(row.IsValueChanged).ToList();

    private string BuildUpdate(EditableRow row)
    {
        var changed = ChangedColumns(row);
        if (changed.Count == 0) return "-- (no changes detected)";

        var setClauses = string.Join(", ", changed.Select(c => $"{Quote(c)} = {(row.Values.GetValueOrDefault(c) is ColumnDefault ? "DEFAULT" : FormatValue(row.Values.GetValueOrDefault(c)))}"));
        var whereClauses = BuildWhere(UpdateWhereColumns(row, changed), row.OriginalValues);
        return $"UPDATE {QuotedTable} SET {setClauses} WHERE {whereClauses};";
    }

    private string BuildDelete(EditableRow row)
    {
        var whereClauses = BuildWhere(_primaryKeyColumns, row.OriginalValues);
        return $"DELETE FROM {QuotedTable} WHERE {whereClauses};";
    }

    private SqlCommandSpec BuildInsertCommand(EditableRow row)
    {
        var insertColumns = InsertColumns(row);
        if (insertColumns.Count == 0)
            return new SqlCommandSpec(BuildDefaultValuesInsert(), new Dictionary<string, object?>());

        var parameters = new Dictionary<string, object?>();
        var values = insertColumns.Select((c, i) => AddParameter(parameters, i, row.Values.GetValueOrDefault(c))).ToList();
        return new SqlCommandSpec(
            $"INSERT INTO {QuotedTable} ({string.Join(", ", insertColumns.Select(Quote))}) VALUES ({string.Join(", ", values)});",
            parameters);
    }

    private SqlCommandSpec? BuildUpdateCommand(EditableRow row)
    {
        var changed = ChangedColumns(row);
        if (changed.Count == 0) return null;

        var parameters = new Dictionary<string, object?>();
        var parameterIndex = 0;
        var setClauses = string.Join(", ", changed.Select(c => row.Values.GetValueOrDefault(c) is ColumnDefault
            ? $"{Quote(c)} = DEFAULT"
            : $"{Quote(c)} = {AddParameter(parameters, parameterIndex++, row.Values.GetValueOrDefault(c))}"));
        var whereClauses = BuildParameterizedWhere(UpdateWhereColumns(row, changed), row.OriginalValues, parameters, ref parameterIndex);

        return new SqlCommandSpec(
            $"UPDATE {QuotedTable} SET {setClauses} WHERE {whereClauses};",
            parameters,
            expectedRecordsAffected: 1);
    }

    private SqlCommandSpec BuildDeleteCommand(EditableRow row)
    {
        var parameters = new Dictionary<string, object?>();
        var parameterIndex = 0;
        var whereClauses = BuildParameterizedWhere(_primaryKeyColumns, row.OriginalValues, parameters, ref parameterIndex);

        return new SqlCommandSpec(
            $"DELETE FROM {QuotedTable} WHERE {whereClauses};",
            parameters,
            expectedRecordsAffected: 1);
    }

    /// <summary>
    /// RAIN-P2-12 — The row's key, then each comparable edited cell not already in the key, so the
    /// UPDATE only matches while those cells still hold the values the user started from.
    /// </summary>
    private List<string> UpdateWhereColumns(EditableRow row, IReadOnlyList<string> changed) =>
        row.OverwritesConcurrentChanges
            ? _primaryKeyColumns.ToList()
            : _primaryKeyColumns
                .Concat(changed.Where(c => !_primaryKeyColumns.Contains(c) && !_nonComparableColumns.Contains(c)))
                .ToList();

    /// <summary>
    /// RAIN-P2-12 — A query for the row's current state in the database, found by the key it was
    /// loaded with, so a row that hit a concurrency conflict can be reloaded.
    /// </summary>
    public TablePageQuery BuildRowQuery(EditableRow row)
    {
        var parameters = new Dictionary<string, object?>();
        var parameterIndex = 0;
        var whereClauses = BuildParameterizedWhere(_primaryKeyColumns, row.OriginalValues, parameters, ref parameterIndex);
        return new TablePageQuery($"SELECT * FROM {QuotedTable} WHERE {whereClauses};", parameters.Values.ToList());
    }

    private string BuildWhere(IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?> values)
    {
        return string.Join(" AND ", columns.Select(c =>
        {
            var val = values.GetValueOrDefault(c);
            return val == null
                ? $"{Quote(c)} IS NULL"
                : $"{Quote(c)} = {FormatValue(val)}";
        }));
    }

    private string BuildParameterizedWhere(
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, object?> values,
        Dictionary<string, object?> parameters,
        ref int parameterIndex)
    {
        var clauses = new List<string>();
        foreach (var c in columns)
        {
            var val = values.GetValueOrDefault(c);
            if (val == null)
            {
                clauses.Add($"{Quote(c)} IS NULL");
                continue;
            }

            clauses.Add($"{Quote(c)} = {AddParameter(parameters, parameterIndex++, val)}");
        }

        return string.Join(" AND ", clauses);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string QuotedTable => Object != null && _capabilities != null ? ObjectNames.Quote(_capabilities, Object)
        : _schemaName is null ? Quote(_tableName) : $"{Quote(_schemaName)}.{Quote(_tableName)}";

    private string Quote(string identifier) => _capabilities?.QuoteIdentifier(identifier)
        ?? $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>Binds a value and returns its placeholder in the provider's style ("@p0", or "?" for positional binding).</summary>
    private string AddParameter(Dictionary<string, object?> parameters, int ordinal, object? value)
    {
        parameters[_capabilities?.CreateParameterName(ordinal) ?? $"@p{ordinal}"] = value;
        return _capabilities?.ParameterPlaceholder(ordinal) ?? $"@p{ordinal}";
    }

    private string FormatValue(object? value)
    {
        if (value == null) return "NULL";
        if (value is bool b) return (_capabilities?.UsesBooleanKeywords ?? false) ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
        if (value is string s) return $"'{s.Replace("'", "''")}'";
        // Culture-invariant: the thread's current culture must never change what numeric/date
        // literal this preview renders (e.g. decimal.ToString() emits "1,5" under a comma-decimal
        // culture, which would silently corrupt the generated SQL literal).
        if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (value is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss.FFFFFFF}'";
        if (value is DateTimeOffset dto) return $"'{dto:yyyy-MM-dd HH:mm:ss.FFFFFFF zzz}'";
        return $"'{Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''")}'";
    }

    private static bool IsNumeric(object value) =>
        value is byte or short or int or long or float or double or decimal;
}
