using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>The default INSERT target: a table source itself, or a query result's single source table, else "exported_result".</summary>
public static class SqlInsertTarget
{
    /// <summary>
    /// The quoted source table when every column comes from the same table; "exported_result" when any column is an
    /// expression or the result joins tables, since its columns need not match one table.
    /// </summary>
    public static string DefaultFor(IReadOnlyList<ColumnInfo> columns, DatabaseProviderCapabilities? dialect)
    {
        var sources = columns.Select(c => (c.SourceSchema, c.SourceTable)).Distinct().ToList();
        if (sources is [(var schema, { Length: > 0 } table)])
            return Qualify(schema, table, dialect);
        return Fallback(dialect);
    }

    /// <summary>
    /// A source table as the INSERT target: schema and name, quoted. The catalog is left out so the script loads
    /// into whichever database it is run against, as the table's CREATE script does.
    /// </summary>
    public static string ForTable(DatabaseObjectIdentity table, DatabaseProviderCapabilities? dialect) =>
        Qualify(table.Schema, table.Name, dialect);

    /// <summary>The target when nothing names one: "exported_result", quoted for the dialect.</summary>
    public static string Fallback(DatabaseProviderCapabilities? dialect) => Quote("exported_result", dialect);

    private static string Qualify(string? schema, string name, DatabaseProviderCapabilities? dialect) =>
        string.IsNullOrEmpty(schema) ? Quote(name, dialect) : $"{Quote(schema, dialect)}.{Quote(name, dialect)}";

    internal static string Quote(string identifier, DatabaseProviderCapabilities? dialect) =>
        dialect?.QuoteIdentifier(identifier) ?? $"\"{identifier.Replace("\"", "\"\"")}\"";
}
