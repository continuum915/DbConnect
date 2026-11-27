using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-16 — The table an editable query result writes back to. <see cref="SourceColumns"/> holds
/// the table column behind each result column, by result ordinal; <see cref="KeyColumns"/> are the
/// table's primary key columns, spelled as in <see cref="SourceColumns"/>.
/// </summary>
public sealed record QueryResultEditTarget(
    string? Schema,
    string Table,
    IReadOnlyList<string> SourceColumns,
    IReadOnlyList<string> KeyColumns);

/// <summary>
/// RAIN-P2-16 — Either an edit target, or why the result is read-only. <see cref="EvaluateAsync"/>
/// decides: every column must come from one source table (per the driver's <see cref="ColumnInfo"/> metadata, which RAIN-P2-15 verifies),
/// no table column may appear twice, and the result must include all of that table's primary key
/// columns so each row can be identified. Anything else is read-only with a reason for the user.
/// </summary>
public sealed record QueryResultEditability(QueryResultEditTarget? Target, string? ReadOnlyReason)
{
    public bool IsEditable => Target != null;

    /// <summary>Read-only only for want of a primary key: the user may opt in to all-column identity.</summary>
    public bool OffersAllColumnIdentity { get; init; }

    private static QueryResultEditability ReadOnly(string reason) => new(null, reason);

    /// <param name="keyColumnsOf">Looks up a table's primary key columns by (schema, table); the
    /// schema is null for a provider without schemas or when the driver reports none.</param>
    /// <param name="allowAllColumnIdentity">Identify rows by all original values when the key is missing (table-tab parity).</param>
    public static async Task<QueryResultEditability> EvaluateAsync(
        QueryResult result,
        DatabaseProviderCapabilities capabilities,
        Func<string?, string, Task<IReadOnlyList<string>>> keyColumnsOf,
        bool allowAllColumnIdentity = false)
    {
        if (result.HasError || !result.HasResultSet)
            return ReadOnly("Read-only: there is no result set to edit.");

        if (!capabilities.ReportsResultColumnSources)
            return ReadOnly(
                "Read-only: this connection doesn't report which table each result column comes from.");

        var columns = result.ColumnInfos;
        var expression = columns.FirstOrDefault(c => c.SourceTable is null || c.SourceColumn is null);
        if (expression != null)
            return ReadOnly(
                $"Read-only: \"{expression.Name}\" is computed, not a column of a table.");

        string? SchemaOf(ColumnInfo column) => capabilities.SupportsSchemas ? column.SourceSchema : null;

        var tables = columns
            .Select(c => (Schema: SchemaOf(c), Table: c.SourceTable!))
            .Distinct(TableComparer.Instance)
            .ToList();
        if (tables.Count > 1)
            return ReadOnly(
                $"Read-only: the result combines columns from {string.Join(", ", tables.Select(Display))}; only single-table results can be edited.");

        var (schema, table) = tables[0];
        var sourceColumns = columns.Select(c => c.SourceColumn!).ToList();
        var repeated = sourceColumns.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null)
            return ReadOnly(
                $"Read-only: column \"{repeated.Key}\" of {Display((schema, table))} appears more than once.");

        var keys = await keyColumnsOf(schema, table);
        var missing = keys.Where(k => !sourceColumns.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0 || missing.Count > 0)
        {
            var reason = keys.Count == 0
                ? $"Read-only: {Display((schema, table))} has no primary key, so its rows can't be told apart."
                : $"Read-only: the result doesn't include {Display((schema, table))}'s key column(s) {string.Join(", ", missing)}.";
            // All-column identity needs every column comparable with =, or rows could match more widely.
            var uncomparable = columns.FirstOrDefault(c => !capabilities.CanCompareType(c.ProviderTypeName));
            if (uncomparable != null)
                return ReadOnly($"{reason} Matching rows by all values isn't possible: \"{uncomparable.Name}\" can't be compared.");
            if (!allowAllColumnIdentity)
                return ReadOnly(reason + " You can allow editing by matching all column values.") with { OffersAllColumnIdentity = true };
            return new QueryResultEditability(new QueryResultEditTarget(schema, table, sourceColumns, []), null);
        }

        var keyColumns = keys
            .Select(k => sourceColumns.First(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new QueryResultEditability(new QueryResultEditTarget(schema, table, sourceColumns, keyColumns), null);
    }

    private static string Display((string? Schema, string Table) table) =>
        table.Schema is null ? table.Table : $"{table.Schema}.{table.Table}";

    private sealed class TableComparer : IEqualityComparer<(string? Schema, string Table)>
    {
        public static readonly TableComparer Instance = new();

        public bool Equals((string? Schema, string Table) x, (string? Schema, string Table) y) =>
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string? Schema, string Table) obj) =>
            HashCode.Combine(
                obj.Schema is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }
}
