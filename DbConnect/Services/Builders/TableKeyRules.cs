using System;
using System.Collections.Generic;

namespace DbConnect.Services.Builders;

/// <summary>
/// How an engine names, renames and replaces primary and unique keys. Each dialect builder owns its
/// engine's rules (<see cref="IDdlBuilder.KeyRules"/>); whether keys can change on an existing table at
/// all comes from the loaded design (<c>TableDesignFeature.ConstraintChanges</c>).
/// </summary>
public sealed record TableKeyRules(string Provider)
{
    /// <summary>A primary key can be given a name (MySQL always calls it PRIMARY).</summary>
    public bool PrimaryKeyNamed { get; init; } = true;

    /// <summary>A loaded key can be renamed without re-creating it.</summary>
    public bool Renames { get; init; }

    /// <summary>The engine makes primary-key columns NOT NULL; the designer asks for it so the reopened columns match.</summary>
    public bool PrimaryKeyNeedsNotNull { get; init; } = true;

    /// <summary>Dropping and re-adding a key is one statement, so a failure leaves the old key in place (MySQL).</summary>
    public bool ReplaceInOneStatement { get; init; }

    /// <summary>A re-created key restates its backing index's method (SQL Server CLUSTERED or NONCLUSTERED).</summary>
    public bool RestatesMethod { get; init; }

    /// <summary>Each of the table's foreign keys needs an index led by its columns (MySQL); a key that is the only one cannot go.</summary>
    public bool ForeignKeysNeedIndex { get; init; }

    /// <summary>Backing-index methods a re-created key keeps (the default, or one the builder restates).</summary>
    public IReadOnlySet<string> KeptMethods { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine's key capabilities, shown on the Constraints tab.</summary>
    public string Summary { get; init; } = "";

    public static TableKeyRules None(string provider) => new(provider)
    {
        Summary = "Keys of existing tables cannot be changed for this provider."
    };

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    public static TableKeyRules PostgreSql { get; } = new("PostgreSQL")
    {
        Renames = true,
        KeptMethods = Set("btree"),
        Summary = "Primary and unique keys can be added, dropped, renamed (RENAME CONSTRAINT) or re-created with new columns. " +
                  "Primary-key columns must be Not Null. Changing a key's columns drops and re-adds it in the save's transaction."
    };

    public static TableKeyRules SqlServer { get; } = new("SQL Server")
    {
        Renames = true,
        RestatesMethod = true,
        KeptMethods = Set("CLUSTERED", "NONCLUSTERED"),
        Summary = "Primary and unique keys can be added, dropped, renamed (sp_rename) or re-created with new columns, keeping " +
                  "CLUSTERED or NONCLUSTERED. Primary-key columns must be Not Null. A key left unnamed gets a generated name."
    };

    public static TableKeyRules MySql { get; } = new("MySQL")
    {
        PrimaryKeyNamed = false,
        Renames = true,
        ReplaceInOneStatement = true,
        ForeignKeysNeedIndex = true,
        KeptMethods = Set("BTREE"),
        Summary = "Primary and unique keys can be added, dropped or re-created with new columns in one ALTER TABLE; unique keys " +
                  "can be renamed (RENAME INDEX). The primary key is always named PRIMARY, and its columns must be Not Null."
    };

    public static TableKeyRules Sqlite { get; } = new("SQLite")
    {
        Renames = true,
        PrimaryKeyNeedsNotNull = false,
        Summary = "Primary and unique keys are part of the table's definition, so any key change rebuilds the table, keeping " +
                  "its data, indexes, triggers and dependent views and checking foreign keys before it commits."
    };

    public static TableKeyRules DuckDb { get; } = new("DuckDB")
    {
        Summary = "Primary and unique keys are declared when the table is created; DuckDB cannot change them on an existing table."
    };
}
