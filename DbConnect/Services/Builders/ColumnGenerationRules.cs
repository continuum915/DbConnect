using System;
using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>Identity and generation changes an engine can make to an existing column, or add with a new one.</summary>
[Flags]
public enum ColumnGenerationChange
{
    None = 0,
    AddIdentity = 1 << 0,
    DropIdentity = 1 << 1,
    IdentityKind = 1 << 2,
    IdentitySeed = 1 << 3,
    IdentityIncrement = 1 << 4,
    /// <summary>An ordinary column becomes generated.</summary>
    AddGeneration = 1 << 5,
    /// <summary>A generated column becomes ordinary, keeping its current values.</summary>
    DropGeneration = 1 << 6,
    Expression = 1 << 7,
    Storage = 1 << 8,
    /// <summary>A new column added to an existing table with identity.</summary>
    NewIdentityColumn = 1 << 9,
    /// <summary>A new column added to an existing table as generated.</summary>
    NewGeneratedColumn = 1 << 10
}

/// <summary>
/// What an engine offers for identity and generated columns, and which changes its ALTER can make.
/// Each dialect builder owns its engine's rules (<see cref="IDdlBuilder.GenerationRules"/>);
/// changes outside <see cref="Alters"/> are blocked before anything runs, with <see cref="Explain"/>.
/// </summary>
public sealed record ColumnGenerationRules(string Provider)
{
    /// <summary>Identity kinds a column may be given; empty when the engine has no identity columns.</summary>
    public IReadOnlyList<TableDesignIdentityKind> IdentityKinds { get; init; } = [];

    /// <summary>The identity has a seed and increment per column.</summary>
    public bool SeedAndIncrement { get; init; }

    /// <summary>Type names (lower case) an identity may have; empty accepts any.</summary>
    public IReadOnlySet<string> IdentityTypes { get; init; } = new HashSet<string>();

    /// <summary>At most one identity column per table.</summary>
    public bool OneIdentityPerTable { get; init; }

    /// <summary>The identity column must lead a key or index (MySQL AUTO_INCREMENT).</summary>
    public bool IdentityNeedsKey { get; init; }

    /// <summary>The identity column must be the table's only primary-key column and typed INTEGER (SQLite).</summary>
    public bool IdentityIsIntegerPrimaryKey { get; init; }

    /// <summary>The engine declares identity columns NOT NULL; the designer asks for it so the reopened column matches.</summary>
    public bool IdentityNeedsNotNull { get; init; }

    public IReadOnlyList<TableDesignGeneratedStorage> Storages { get; init; } = [];
    public TableDesignGeneratedStorage DefaultStorage { get; init; } = TableDesignGeneratedStorage.Virtual;

    /// <summary>Only stored generated columns may be NOT NULL (SQL Server PERSISTED).</summary>
    public bool OnlyStoredGeneratedNotNull { get; init; }

    /// <summary>Computed columns have no declared type (SQL Server); the Type field is ignored for them.</summary>
    public bool GeneratedColumnsUntyped { get; init; }

    public ColumnGenerationChange Alters { get; init; }

    /// <summary>Only STORED generated columns may become ordinary, or ordinary ones generated (MySQL).</summary>
    public bool OnlyStoredConvertible { get; init; }

    /// <summary>
    /// The engine refuses to change the type or collation of a column a generated column reads
    /// (PostgreSQL, SQL Server, DuckDB); the edit is blocked before anything runs.
    /// </summary>
    public bool TypeLockedByGeneratedColumns { get; init; }

    /// <summary>The engine's capability, shown above the column grid.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Why the engine cannot make <paramref name="change"/>; shown when an edit needs it.</summary>
    public Func<ColumnGenerationChange, string> Explain { get; init; } = _ => "";

    public bool Allows(ColumnGenerationChange change) => (Alters & change) == change;

    public static ColumnGenerationRules None(string provider) => new(provider)
    {
        Summary = "Identity and generated columns: not available for this provider.",
        Explain = _ => $"identity and generated columns cannot be edited for {provider}."
    };

    public static ColumnGenerationRules PostgreSql { get; } = new("PostgreSQL")
    {
        IdentityKinds = [TableDesignIdentityKind.Always, TableDesignIdentityKind.ByDefault],
        SeedAndIncrement = true,
        IdentityTypes = Set("smallint", "integer", "bigint", "int", "int2", "int4", "int8"),
        IdentityNeedsNotNull = true,
        // VIRTUAL needs PostgreSQL 18; the designer offers STORED, which every supported version has.
        Storages = [TableDesignGeneratedStorage.Stored],
        DefaultStorage = TableDesignGeneratedStorage.Stored,
        TypeLockedByGeneratedColumns = true,
        Alters = ColumnGenerationChange.AddIdentity | ColumnGenerationChange.DropIdentity | ColumnGenerationChange.IdentityKind
                 | ColumnGenerationChange.IdentitySeed | ColumnGenerationChange.IdentityIncrement | ColumnGenerationChange.DropGeneration
                 | ColumnGenerationChange.Expression | ColumnGenerationChange.NewIdentityColumn | ColumnGenerationChange.NewGeneratedColumn,
        Summary = "Identity: ALWAYS or BY DEFAULT with seed and increment; changing the seed sets START WITH for a later RESTART and " +
                  "keeps the current sequence value. Generated columns: STORED. An existing column cannot become generated, and a " +
                  "generated column's expression can be changed only on PostgreSQL 17 or later.",
        Explain = change => change switch
        {
            ColumnGenerationChange.AddGeneration => "PostgreSQL cannot make an existing column generated; add a new generated column instead.",
            ColumnGenerationChange.Storage => "PostgreSQL cannot change whether a generated column is stored.",
            _ => "PostgreSQL cannot make this change to an existing column."
        }
    };

    public static ColumnGenerationRules SqlServer { get; } = new("SQL Server")
    {
        IdentityKinds = [TableDesignIdentityKind.Identity],
        SeedAndIncrement = true,
        IdentityTypes = Set("tinyint", "smallint", "int", "bigint", "decimal", "numeric"),
        OneIdentityPerTable = true,
        IdentityNeedsNotNull = true,
        Storages = [TableDesignGeneratedStorage.Virtual, TableDesignGeneratedStorage.Stored],
        OnlyStoredGeneratedNotNull = true,
        GeneratedColumnsUntyped = true,
        TypeLockedByGeneratedColumns = true,
        Alters = ColumnGenerationChange.NewIdentityColumn | ColumnGenerationChange.NewGeneratedColumn,
        Summary = "Identity: IDENTITY(seed, increment), one per table. Computed columns: virtual or PERSISTED (stored), typed by their " +
                  "expression. SQL Server cannot add, remove or change identity or computation on an existing column.",
        Explain = change => change is ColumnGenerationChange.AddIdentity or ColumnGenerationChange.DropIdentity
                or ColumnGenerationChange.IdentitySeed or ColumnGenerationChange.IdentityIncrement or ColumnGenerationChange.IdentityKind
            ? "SQL Server cannot add, remove or change IDENTITY on an existing column."
            : "SQL Server cannot change the computation of an existing column; drop it and add a new computed column."
    };

    public static ColumnGenerationRules MySql { get; } = new("MySQL")
    {
        IdentityKinds = [TableDesignIdentityKind.AutoIncrement],
        IdentityTypes = Set("tinyint", "smallint", "mediumint", "int", "integer", "bigint"),
        OneIdentityPerTable = true,
        IdentityNeedsKey = true,
        IdentityNeedsNotNull = true,
        Storages = [TableDesignGeneratedStorage.Virtual, TableDesignGeneratedStorage.Stored],
        OnlyStoredConvertible = true,
        Alters = ColumnGenerationChange.AddIdentity | ColumnGenerationChange.DropIdentity | ColumnGenerationChange.AddGeneration
                 | ColumnGenerationChange.DropGeneration | ColumnGenerationChange.Expression | ColumnGenerationChange.NewGeneratedColumn,
        Summary = "Identity: AUTO_INCREMENT on a key column, one per table; its next value is a table option, not a column seed. " +
                  "Generated columns: VIRTUAL or STORED. Only stored generated columns can become ordinary (or ordinary ones stored " +
                  "generated); MySQL cannot change whether a generated column is stored.",
        Explain = change => change switch
        {
            ColumnGenerationChange.Storage => "MySQL cannot change whether a generated column is stored.",
            ColumnGenerationChange.AddGeneration or ColumnGenerationChange.DropGeneration =>
                "MySQL can turn only STORED generated columns into ordinary ones and back, not VIRTUAL ones.",
            ColumnGenerationChange.NewIdentityColumn =>
                "MySQL needs an AUTO_INCREMENT column to be a key when it is added; add it with the key in a separate script.",
            _ => "MySQL has no per-column seed or increment; AUTO_INCREMENT continues from the table's counter."
        }
    };

    public static ColumnGenerationRules Sqlite { get; } = new("SQLite")
    {
        // A rowid alias is what an INTEGER PRIMARY KEY is without AUTOINCREMENT; choosing it plans no change by itself.
        IdentityKinds = [TableDesignIdentityKind.RowIdAlias, TableDesignIdentityKind.AutoIncrement],
        IdentityIsIntegerPrimaryKey = true,
        Storages = [TableDesignGeneratedStorage.Virtual, TableDesignGeneratedStorage.Stored],
        // The table is rebuilt, so any generation change can be made; AUTOINCREMENT needs the existing INTEGER PRIMARY KEY.
        Alters = ColumnGenerationChange.AddIdentity | ColumnGenerationChange.DropIdentity | ColumnGenerationChange.AddGeneration
                 | ColumnGenerationChange.DropGeneration | ColumnGenerationChange.Expression | ColumnGenerationChange.Storage
                 | ColumnGenerationChange.NewGeneratedColumn,
        Summary = "Identity: AUTOINCREMENT on the INTEGER PRIMARY KEY (which is otherwise an alias of the rowid); the rebuild keeps " +
                  "the table's sequence. Generated columns: VIRTUAL or STORED; changes rebuild the table.",
        Explain = _ => "SQLite gives AUTOINCREMENT only to the INTEGER PRIMARY KEY, and the designer cannot change the primary key."
    };

    public static ColumnGenerationRules DuckDb { get; } = new("DuckDB")
    {
        Storages = [TableDesignGeneratedStorage.Virtual],
        TypeLockedByGeneratedColumns = true,
        Summary = "DuckDB has no identity columns: use a sequence with DEFAULT nextval('sequence'). Generated columns: VIRTUAL, " +
                  "declared when the table is created; DuckDB cannot add or change them on an existing table.",
        Explain = change => change is ColumnGenerationChange.AddIdentity or ColumnGenerationChange.NewIdentityColumn
            ? "DuckDB has no identity columns; use a sequence with DEFAULT nextval('sequence')."
            : "DuckDB cannot add or change generated columns on an existing table."
    };

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
