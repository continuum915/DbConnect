using System.Collections.Generic;

namespace DbConnect.Services;

/// <summary>
/// How a data script inserts the values a table's identity and serial columns already hold, and keeps them generating
/// after the load. Format placeholders: {0} the quoted table, {1} the quoted column, {2} the quoted table as a string
/// literal, {3} the column name as a string literal. Engines that take explicit values and move their counters past
/// them on their own (MySQL AUTO_INCREMENT, SQLite) need none of this.
/// </summary>
public sealed record IdentityValueLoading
{
    /// <summary>Run before a table's INSERTs when it has an identity column (SQL Server: SET IDENTITY_INSERT … ON).</summary>
    public string? Enable { get; init; }

    /// <summary>Run after them.</summary>
    public string? Disable { get; init; }

    /// <summary>Written between the column list and VALUES when the table has an identity column (PostgreSQL: OVERRIDING SYSTEM VALUE).</summary>
    public string? InsertOverride { get; init; }

    /// <summary>Run per identity or owned-sequence column after a table's rows load, to move its sequence past them.</summary>
    public string? Advance { get; init; }

    /// <summary>Column types whose values only the engine writes (SQL Server rowversion); the INSERTs leave them out.</summary>
    public IReadOnlyCollection<string> EngineWrittenTypeNames { get; init; } = [];

    /// <summary>Fills one of these statements' placeholders for a quoted table and, for <see cref="Advance"/>, a column name.</summary>
    public static string Format(string statement, string table, string? column, DatabaseProviderCapabilities dialect) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, table,
            column == null ? "" : dialect.QuoteIdentifier(column), SqlLiteral.Format(table, dialect),
            column == null ? "" : SqlLiteral.Format(column, dialect));
}
