using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services.Builders;

/// <summary>Which modifier a type takes in parentheses after its name.</summary>
public enum ColumnTypeModifier
{
    /// <summary>The type takes no modifier.</summary>
    None,
    /// <summary>A length, e.g. VARCHAR(40), or max where the engine allows it.</summary>
    Length,
    /// <summary>A precision and an optional scale, e.g. DECIMAL(10,2).</summary>
    PrecisionScale,
    /// <summary>A precision alone, e.g. fractional seconds in TIME(3) or FLOAT(24).</summary>
    Precision
}

/// <summary>What one type takes on one engine. Ranges are inclusive.</summary>
public sealed record ColumnTypeRule(ColumnTypeModifier Modifier, int Min = 0, int Max = int.MaxValue)
{
    /// <summary>The length may be max (SQL Server).</summary>
    public bool AllowsMaxLength { get; init; }
    /// <summary>The type stores text and can have a collation.</summary>
    public bool TakesCollation { get; init; }
    /// <summary>Values are padded to the length (CHAR, BINARY, BIT), so a longer length changes stored values.</summary>
    public bool FixedLength { get; init; }
    /// <summary>The engine rejects the type without its modifier (MySQL VARCHAR).</summary>
    public bool RequiresModifier { get; init; }
    /// <summary>The largest scale, when lower than the precision allows.</summary>
    public int? MaxScale { get; init; }
}

/// <summary>
/// An engine's rules for type modifiers and collations, used to split a loaded type into its
/// name and modifiers and to validate edits before any SQL runs. Types missing from the rules
/// are not guessed at: their declared text is kept whole and modifiers cannot be edited separately.
/// </summary>
public sealed class ColumnTypeRules
{
    // Declared before TrailingWords, which is initialized from it.
    /// <summary>MySQL's sign words: they follow the modifier but do not change which modifier the type takes.</summary>
    private static readonly string[] SignWords = [" unsigned", " signed", " zerofill"];

    /// <summary>Words that follow a type's modifier, e.g. TIMESTAMP(3) WITH TIME ZONE or DECIMAL(10,2) UNSIGNED.</summary>
    internal static readonly string[] TrailingWords = [" with time zone", " without time zone", .. SignWords];


    private readonly Dictionary<string, ColumnTypeRule> _rules;

    private ColumnTypeRules(string providerName, Dictionary<string, ColumnTypeRule> rules, bool collationOnAnyType)
    {
        ProviderName = providerName;
        _rules = rules;
        CollationOnAnyType = collationOnAnyType;
    }

    public string ProviderName { get; }

    /// <summary>Any column may have a collation (SQLite).</summary>
    public bool CollationOnAnyType { get; }

    /// <summary>The rule for a type name without modifiers, or null when the rules do not know the type.</summary>
    public ColumnTypeRule? For(string label)
    {
        var name = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        while (true)
        {
            if (_rules.TryGetValue(name, out var rule)) return rule;
            var word = SignWords.FirstOrDefault(w => name.EndsWith(w, StringComparison.OrdinalIgnoreCase));
            if (word == null) return null;
            name = name[..^word.Length];
        }
    }

    public static ColumnTypeRules Unknown(string providerName) => new(providerName, new(StringComparer.OrdinalIgnoreCase), false);

    public static ColumnTypeRules PostgreSql { get; } = Build("PostgreSQL", false, rules =>
    {
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 10485760) { TakesCollation = true }, "character varying", "varchar");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 10485760) { TakesCollation = true, FixedLength = true }, "character", "char");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None) { TakesCollation = true }, "text", "name", "citext");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 83886080) { FixedLength = true }, "bit");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 83886080), "bit varying", "varbit");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.PrecisionScale, 1, 1000), "numeric", "decimal");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Precision, 0, 6), "timestamp without time zone", "timestamp with time zone",
            "time without time zone", "time with time zone", "timestamp", "timestamptz", "time", "timetz", "interval");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None), "smallint", "integer", "int", "bigint", "int2", "int4", "int8",
            "real", "double precision", "float4", "float8", "boolean", "bool", "date", "uuid", "json", "jsonb", "bytea", "money",
            "inet", "cidr", "macaddr", "xml", "oid", "serial", "bigserial", "smallserial");
    });

    public static ColumnTypeRules SqlServer { get; } = Build("SQL Server", false, rules =>
    {
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 8000) { AllowsMaxLength = true, TakesCollation = true }, "varchar");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 8000) { TakesCollation = true, FixedLength = true }, "char");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 4000) { AllowsMaxLength = true, TakesCollation = true }, "nvarchar");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 4000) { TakesCollation = true, FixedLength = true }, "nchar");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 8000) { AllowsMaxLength = true }, "varbinary");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 8000) { FixedLength = true }, "binary");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None) { TakesCollation = true }, "text", "ntext", "sysname");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.PrecisionScale, 1, 38), "decimal", "numeric");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Precision, 0, 7), "datetime2", "time", "datetimeoffset");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Precision, 1, 53), "float");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None), "int", "bigint", "smallint", "tinyint", "bit", "money", "smallmoney",
            "real", "date", "datetime", "smalldatetime", "uniqueidentifier", "xml", "image", "timestamp", "rowversion", "sql_variant",
            "hierarchyid", "geography", "geometry");
    });

    public static ColumnTypeRules MySql { get; } = Build("MySQL", false, rules =>
    {
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 0, 65535) { TakesCollation = true, RequiresModifier = true }, "varchar");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 0, 255) { TakesCollation = true, FixedLength = true }, "char");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 0, 65535) { RequiresModifier = true }, "varbinary");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 0, 255) { FixedLength = true }, "binary");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1, 64) { FixedLength = true }, "bit");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None) { TakesCollation = true }, "text", "tinytext", "mediumtext", "longtext");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.PrecisionScale, 1, 65) { MaxScale = 30 }, "decimal", "numeric");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Precision, 0, 6), "datetime", "timestamp", "time");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None), "int", "integer", "bigint", "smallint", "tinyint", "mediumint",
            "float", "double", "date", "year", "json", "blob", "tinyblob", "mediumblob", "longblob", "boolean", "bool", "geometry");
    });

    public static ColumnTypeRules DuckDb { get; } = Build("DuckDB", false, rules =>
    {
        // DuckDB accepts VARCHAR(n) but does not store the length, so it is not offered.
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None) { TakesCollation = true }, "varchar", "text", "string");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.PrecisionScale, 1, 38), "decimal", "numeric");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None), "integer", "int", "bigint", "smallint", "tinyint", "hugeint",
            "utinyint", "usmallint", "uinteger", "ubigint", "float", "real", "double", "boolean", "date", "time", "timestamp",
            "timestamp with time zone", "interval", "blob", "uuid", "json");
    });

    /// <summary>SQLite keeps the declared type as written and enforces neither lengths nor precision; any column may have a collation.</summary>
    public static ColumnTypeRules Sqlite { get; } = Build("SQLite", true, rules =>
    {
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.Length, 1), "varchar", "char", "nvarchar", "nchar", "character",
            "character varying", "varying character", "native character");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.PrecisionScale, 1), "decimal", "numeric");
        Add(rules, new ColumnTypeRule(ColumnTypeModifier.None), "integer", "int", "text", "real", "blob", "double", "float", "boolean");
    });

    private static ColumnTypeRules Build(string provider, bool collationOnAnyType, Action<Dictionary<string, ColumnTypeRule>> add)
    {
        var rules = new Dictionary<string, ColumnTypeRule>(StringComparer.OrdinalIgnoreCase);
        add(rules);
        return new ColumnTypeRules(provider, rules, collationOnAnyType);
    }

    private static void Add(Dictionary<string, ColumnTypeRule> rules, ColumnTypeRule rule, params string[] names)
    {
        foreach (var name in names) rules[name] = rule;
    }
}
