using System.Collections.Generic;

namespace DbConnect.Services.Builders;

/// <summary>
/// The referential actions an engine accepts and how it renames and replaces foreign keys. Each
/// dialect builder owns its engine's rules (<see cref="IDdlBuilder.ForeignKeyRules"/>); whether
/// foreign keys can change on an existing table at all comes from the loaded design.
/// </summary>
public sealed record TableForeignKeyRules(string Provider)
{
    /// <summary>ON DELETE / ON UPDATE actions the engine accepts.</summary>
    public IReadOnlyList<string> Actions { get; init; } = ["NO ACTION"];

    /// <summary>A loaded foreign key can be renamed without re-creating it.</summary>
    public bool Renames { get; init; }

    /// <summary>The referenced columns must be a key in its own column order (InnoDB); elsewhere any order of a key's columns.</summary>
    public bool ReferencedKeyInOrder { get; init; }

    /// <summary>The referenced table must be in the same schema as the table (SQLite).</summary>
    public bool SameSchemaOnly { get; init; }

    /// <summary>The engine's foreign-key capabilities, shown on the Foreign Keys tab.</summary>
    public string Summary { get; init; } = "";

    private static readonly string[] All = ["NO ACTION", "RESTRICT", "CASCADE", "SET NULL", "SET DEFAULT"];

    public static TableForeignKeyRules None(string provider) => new(provider)
    {
        Summary = "Foreign keys of existing tables cannot be changed for this provider."
    };

    public static TableForeignKeyRules PostgreSql { get; } = new("PostgreSQL")
    {
        Actions = All,
        Renames = true,
        Summary = "Foreign keys can reference any table in the database, be added, dropped, renamed (RENAME CONSTRAINT) or re-created " +
                  "with new columns, table or actions; existing rows are checked when one is added."
    };

    public static TableForeignKeyRules SqlServer { get; } = new("SQL Server")
    {
        Actions = ["NO ACTION", "CASCADE", "SET NULL", "SET DEFAULT"],
        Renames = true,
        Summary = "Foreign keys can reference any table in the database, be added, dropped, renamed (sp_rename) or re-created; " +
                  "SQL Server has no RESTRICT (NO ACTION is checked immediately)."
    };

    public static TableForeignKeyRules MySql { get; } = new("MySQL")
    {
        Actions = ["NO ACTION", "RESTRICT", "CASCADE", "SET NULL"],
        ReferencedKeyInOrder = true,
        // MySQL refuses to drop and add a foreign key of the same name in one ALTER TABLE (error 1826).
        Summary = "Foreign keys can be added, dropped or re-created; re-creating one is two statements (drop, then add), and MySQL " +
                  "DDL is not transactional, so a failed add is reported with the drop already applied. Renaming one re-creates it. " +
                  "InnoDB rejects SET DEFAULT."
    };

    public static TableForeignKeyRules Sqlite { get; } = new("SQLite")
    {
        Actions = All,
        Renames = true,
        SameSchemaOnly = true,
        Summary = "Foreign keys are part of the table's definition, so any change rebuilds the table and checks every foreign key " +
                  "before it commits. They reference tables in the same database and are enforced when PRAGMA foreign_keys is on."
    };

    public static TableForeignKeyRules DuckDb { get; } = new("DuckDB")
    {
        Summary = "Foreign keys are declared when the table is created, with NO ACTION; DuckDB cannot change them later."
    };
}
