using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>
/// How an engine renames and re-creates independent indexes, which index properties the designer edits
/// (expression keys, filters, included columns and the methods the builder writes), and which loaded
/// properties a re-created index restates. Each dialect builder owns its engine's rules
/// (<see cref="IDdlBuilder.IndexRules"/>); a re-creation that would lose anything else is blocked.
/// </summary>
public sealed record TableIndexRules(string Provider)
{
    /// <summary>A loaded index can be renamed without re-creating it.</summary>
    public bool Renames { get; init; }

    /// <summary>Dropping and re-adding an index is one statement, so a failure leaves the old one (MySQL).</summary>
    public bool ReplaceInOneStatement { get; init; }

    /// <summary>Key parts can be descending.</summary>
    public bool Descending { get; init; } = true;

    /// <summary>Key parts can be expressions: they are typed and edited, and a re-created index restates loaded ones.</summary>
    public bool Expressions { get; init; }

    /// <summary>An index can have a filter (WHERE): it is edited, and a re-created index restates a loaded one.</summary>
    public bool Filters { get; init; }

    /// <summary>An index can have included (non-key) columns: they are edited, and a re-created index restates loaded ones.</summary>
    public bool IncludedColumns { get; init; }

    /// <summary>
    /// The methods the designer offers, the first being a new index's; empty when the engine's index method is not
    /// edited. A loaded index with another method keeps it read-only.
    /// </summary>
    public IReadOnlyList<TableIndexMethod> Methods { get; init; } = [];

    /// <summary>A re-created index restates any loaded method (USING ...).</summary>
    public bool RestatesAnyMethod { get; init; }

    /// <summary>Loaded methods a re-created index keeps (the engine's default, or one the builder restates).</summary>
    public IReadOnlySet<string> KeptMethods { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An index can be dropped and one of the same name created in the same transaction. DuckDB 1.3 cannot:
    /// its process fails natively at COMMIT (probed through DuckDB.NET 1.3.0), so such a plan is blocked.
    /// </summary>
    public bool ReusesNameInOneTransaction { get; init; } = true;

    /// <summary>Each of the table's foreign keys needs an index led by its columns (MySQL); an index that is the only one cannot go.</summary>
    public bool ForeignKeysNeedIndex { get; init; }

    /// <summary>The engine's index capabilities, shown on the Indexes tab.</summary>
    public string Summary { get; init; } = "";

    public bool KeepsMethod(string? method) => method == null || RestatesAnyMethod || KeptMethods.Contains(method) || Method(method) != null;

    /// <summary>The offered method of that name; null when the designer does not offer it.</summary>
    public TableIndexMethod? Method(string? name) =>
        name == null ? null : Methods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static TableIndexRules None(string provider) => new(provider)
    {
        Summary = "Indexes of existing tables cannot be changed for this provider."
    };

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private const string Preserved = " Engine settings and comments are kept; an index whose settings cannot be restated is not re-created.";

    public static TableIndexRules PostgreSql { get; } = new("PostgreSQL")
    {
        Renames = true,
        Expressions = true,
        Filters = true,
        IncludedColumns = true,
        RestatesAnyMethod = true,
        // Only B-tree indexes are unique or ordered; GiST (PostgreSQL 12+) and SP-GiST (14+) take INCLUDE; hash and SP-GiST have one key.
        Methods =
        [
            new("btree"),
            new("hash") { Unique = false, Descending = false, Included = false, MultipleKeys = false },
            new("gist") { Unique = false, Descending = false },
            new("spgist") { Unique = false, Descending = false, MultipleKeys = false },
            new("gin") { Unique = false, Descending = false, Included = false },
            new("brin") { Unique = false, Descending = false, Included = false }
        ],
        Summary = "Indexes can be added, dropped, renamed (ALTER INDEX … RENAME) or re-created with new keys, expressions, directions, " +
                  "uniqueness, included columns, filter or method in the save's transaction." + Preserved
    };

    public static TableIndexRules SqlServer { get; } = new("SQL Server")
    {
        Renames = true,
        Filters = true,
        IncludedColumns = true,
        KeptMethods = Set("CLUSTERED", "NONCLUSTERED"),
        // The clustered index is the table's row order: one per table, without included columns or a filter.
        Methods =
        [
            new("NONCLUSTERED"),
            new("CLUSTERED") { Included = false, Filter = false, OnePerTable = true }
        ],
        Summary = "Indexes can be added, dropped, renamed (sp_rename) or re-created with new keys, directions, uniqueness, included " +
                  "columns, filter, or CLUSTERED or NONCLUSTERED, in the save's transaction. SQL Server keys are columns, not expressions." +
                  Preserved
    };

    public static TableIndexRules MySql { get; } = new("MySQL")
    {
        Renames = true,
        ReplaceInOneStatement = true,
        ForeignKeysNeedIndex = true,
        Expressions = true,
        KeptMethods = Set("BTREE"),
        Summary = "Indexes can be added, dropped, renamed (RENAME INDEX) or re-created with new keys, expressions (functional key parts), " +
                  "directions or uniqueness in one ALTER TABLE. MySQL lists a unique index over plain columns as a unique key. MySQL " +
                  "indexes have no filters or included columns; FULLTEXT, SPATIAL and HASH indexes are kept read-only." + Preserved
    };

    public static TableIndexRules Sqlite { get; } = new("SQLite")
    {
        Expressions = true,
        Filters = true,
        Summary = "Indexes can be added, dropped or re-created with new keys, expressions, directions, uniqueness or WHERE filter; " +
                  "renaming one re-creates it. SQLite indexes have no included columns or methods." + Preserved
    };

    public static TableIndexRules DuckDb { get; } = new("DuckDB")
    {
        Descending = false,
        Expressions = true,
        ReusesNameInOneTransaction = false,
        Summary = "Indexes can be added, dropped or re-created with new keys, expressions or uniqueness under a new name; renaming one " +
                  "re-creates it. DuckDB indexes have no descending keys, filters, included columns or methods, and an index cannot be " +
                  "dropped and re-created under the same name in one save." + Preserved
    };
}

/// <summary>An index method the designer offers, and what an index using it can have.</summary>
public sealed record TableIndexMethod(string Name)
{
    public bool Unique { get; init; } = true;
    public bool Descending { get; init; } = true;
    public bool Included { get; init; } = true;
    public bool Filter { get; init; } = true;

    /// <summary>The index can have more than one key part.</summary>
    public bool MultipleKeys { get; init; } = true;

    /// <summary>Only one index of a table can use it (SQL Server's clustered index).</summary>
    public bool OnePerTable { get; init; }
}

/// <summary>Index SQL shared by the dialect builders.</summary>
internal static class DdlIndexSql
{
    /// <summary>The key parts: quoted columns or parenthesised expressions, each with DESC when descending.</summary>
    public static string Parts(TableDesignIndex index, Func<string, string> quote) =>
        string.Join(", ", index.Parts.Select(p => (p.Column != null ? quote(p.Column) : Parenthesised(p.Expression!)) + (p.Descending ? " DESC" : "")));

    /// <summary>The expression in parentheses, unless one pair already encloses all of it (re-creating must not add more each time).</summary>
    public static string Parenthesised(string expression)
    {
        var text = expression.Trim();
        if (!text.StartsWith('(') || !text.EndsWith(')')) return $"({text})";
        var depth = 0;
        foreach (var token in SqlTokenizer.Tokenize(text, SqlBatchDialect.Standard))
        {
            if (token.Kind != SqlTokenKind.Symbol) continue;
            if (token.Text == "(") depth++;
            else if (token.Text == ")" && --depth == 0) return token.Start + 1 == text.Length ? text : $"({text})";
        }
        return $"({text})";
    }

    public static string Unique(TableDesignIndex index) => index.IsUnique ? "UNIQUE " : "";

    /// <summary>
    /// "CREATE [UNIQUE] [kind ]INDEX name ON table[ USING method] (parts)[ INCLUDE (...)][ WHERE filter];". The index
    /// carries included columns and a filter only where the engine has them and its rules restate them.
    /// </summary>
    public static string Create(TableDesignIndex index, string table, Func<string, string> quote, string kind = "", string method = "") =>
        $"CREATE {Unique(index)}{kind}INDEX {quote(index.Name)} ON {table}{method} ({Parts(index, quote)})" +
        (index.IncludedColumns.Count > 0 ? $" INCLUDE ({string.Join(", ", index.IncludedColumns.Select(quote))})" : "") +
        (index.Filter != null ? $" WHERE {index.Filter}" : "") + ";";
}
