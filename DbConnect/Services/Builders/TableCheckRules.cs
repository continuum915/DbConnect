namespace DbConnect.Services.Builders;

/// <summary>
/// How an engine renames and replaces check constraints. Each dialect builder owns its engine's rules
/// (<see cref="IDdlBuilder.CheckRules"/>); whether checks can change on an existing table at all comes from
/// the loaded design.
/// </summary>
public sealed record TableCheckRules(string Provider)
{
    /// <summary>A loaded check can be renamed without re-creating it.</summary>
    public bool Renames { get; init; }

    /// <summary>Dropping and re-adding a check is one statement, so a failure leaves the old one (MySQL).</summary>
    public bool ReplaceInOneStatement { get; init; }

    /// <summary>The engine's check capabilities, shown on the Constraints tab.</summary>
    public string Summary { get; init; } = "";

    public static TableCheckRules None(string provider) => new(provider)
    {
        Summary = "Checks of existing tables cannot be changed for this provider."
    };

    public static TableCheckRules PostgreSql { get; } = new("PostgreSQL")
    {
        Renames = true,
        Summary = "Checks can be added, dropped, renamed (RENAME CONSTRAINT) or re-created with a new expression; existing rows are " +
                  "checked when one is added. The server judges the expression; DbConnect only rejects obviously malformed text."
    };

    public static TableCheckRules SqlServer { get; } = new("SQL Server")
    {
        Renames = true,
        Summary = "Checks can be added, dropped, renamed (sp_rename) or re-created; existing rows are checked when one is added. " +
                  "A re-created column check becomes a table check, with the same effect."
    };

    public static TableCheckRules MySql { get; } = new("MySQL")
    {
        ReplaceInOneStatement = true,
        Summary = "Checks can be added, dropped or re-created in one ALTER TABLE; renaming one re-creates it. NOT ENFORCED checks " +
                  "can only be dropped."
    };

    public static TableCheckRules Sqlite { get; } = new("SQLite")
    {
        Renames = true,
        Summary = "Checks are part of the table's definition, so any change rebuilds the table; a rebuild that existing rows " +
                  "violate is rolled back. A re-created column check becomes a table check, with the same effect."
    };

    public static TableCheckRules DuckDb { get; } = new("DuckDB")
    {
        Summary = "Checks are declared when the table is created; DuckDB cannot change them later."
    };
}
