using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DbConnect.Services;

public class DdlTranslationService
{
    private static readonly Dictionary<string, Dictionary<string, string>> TypeMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLite->PostgreSQL"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["INTEGER"] = "BIGINT",
            ["TEXT"] = "TEXT",
            ["REAL"] = "DOUBLE PRECISION",
            ["BLOB"] = "BYTEA",
            ["NUMERIC"] = "NUMERIC"
        },
        ["SQLite->SQL Server"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["INTEGER"] = "BIGINT",
            ["TEXT"] = "NVARCHAR(MAX)",
            ["REAL"] = "FLOAT",
            ["BLOB"] = "VARBINARY(MAX)",
            ["NUMERIC"] = "NUMERIC"
        },
        ["PostgreSQL->SQLite"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SERIAL"] = "INTEGER",
            ["BIGSERIAL"] = "INTEGER",
            ["BOOLEAN"] = "INTEGER",
            ["BOOL"] = "INTEGER",
            ["TIMESTAMP"] = "TEXT",
            ["TIMESTAMPTZ"] = "TEXT"
        },
        ["SQL Server->SQLite"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NVARCHAR"] = "TEXT",
            ["VARCHAR"] = "TEXT",
            ["BIT"] = "INTEGER",
            ["INT"] = "INTEGER"
        }
    };

    public (string TranslatedDdl, List<string> Warnings) TranslateDdl(string ddl, string sourceProvider, string targetProvider)
    {
        var warnings = new List<string>();
        var key = $"{NormalizeProvider(sourceProvider)}->{NormalizeProvider(targetProvider)}";

        var result = ddl;

        if (TypeMaps.TryGetValue(key, out var typeMap))
        {
            // Apply type mappings (whole-word replacement, case-insensitive)
            foreach (var (from, to) in typeMap)
            {
                result = Regex.Replace(result, $@"\b{Regex.Escape(from)}\b", to, RegexOptions.IgnoreCase);
            }
        }
        else
        {
            warnings.Add($"No type mapping found for {sourceProvider} → {targetProvider}.");
        }

        // Quote style translation — always applied regardless of type mapping
        if (NormalizeProvider(targetProvider) == "SQL Server")
        {
            // double-quotes → brackets
            result = Regex.Replace(result, "\"([^\"]+)\"", "[$1]");
        }
        else if (NormalizeProvider(targetProvider) is "PostgreSQL" or "SQLite")
        {
            // brackets → double-quotes
            result = Regex.Replace(result, @"\[([^\]]+)\]", "\"$1\"");
        }

        // Handle INT IDENTITY → INTEGER for SQL Server→SQLite
        if (key == "SQL Server->SQLite")
        {
            result = Regex.Replace(result, @"\bINT\s+IDENTITY\b", "INTEGER", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bIDENTITY\s*\(\d+\s*,\s*\d+\)\b", string.Empty, RegexOptions.IgnoreCase);
        }

        return (result, warnings);
    }

    public List<string> GetCompatibilityReport(string ddl, string targetProvider)
    {
        var issues = new List<string>();
        var target = NormalizeProvider(targetProvider);

        if (target == "SQL Server")
        {
            if (Regex.IsMatch(ddl, @"\bSERIAL\b", RegexOptions.IgnoreCase))
                issues.Add("SERIAL is not supported in SQL Server. Use IDENTITY(1,1) instead.");
            if (Regex.IsMatch(ddl, @"\bBIGSERIAL\b", RegexOptions.IgnoreCase))
                issues.Add("BIGSERIAL is not supported in SQL Server. Use BIGINT IDENTITY(1,1) instead.");
            if (Regex.IsMatch(ddl, @"\bBOOLEAN\b", RegexOptions.IgnoreCase))
                issues.Add("BOOLEAN is not natively supported in SQL Server. Use BIT instead.");
            if (Regex.IsMatch(ddl, @"\bBYTEA\b", RegexOptions.IgnoreCase))
                issues.Add("BYTEA is not supported in SQL Server. Use VARBINARY(MAX) instead.");
        }
        else if (target == "SQLite")
        {
            if (Regex.IsMatch(ddl, @"\bSERIAL\b", RegexOptions.IgnoreCase))
                issues.Add("SERIAL is not supported in SQLite. Use INTEGER PRIMARY KEY AUTOINCREMENT.");
            if (Regex.IsMatch(ddl, @"\bARRAY\b", RegexOptions.IgnoreCase))
                issues.Add("ARRAY types are not supported in SQLite.");
            if (Regex.IsMatch(ddl, @"\bJSON\b", RegexOptions.IgnoreCase))
                issues.Add("JSON type is stored as TEXT in SQLite.");
        }
        else if (target == "PostgreSQL")
        {
            if (Regex.IsMatch(ddl, @"\bIDENTITY\b", RegexOptions.IgnoreCase))
                issues.Add("IDENTITY columns should be replaced with SERIAL or GENERATED ALWAYS AS IDENTITY in PostgreSQL.");
            if (Regex.IsMatch(ddl, @"\bNVARCHAR\b", RegexOptions.IgnoreCase))
                issues.Add("NVARCHAR is not a native PostgreSQL type. Use VARCHAR or TEXT.");
        }

        return issues;
    }

    private static string NormalizeProvider(string provider) =>
        provider.ToUpperInvariant() switch
        {
            "POSTGRESQL" or "POSTGRES" or "PG" => "PostgreSQL",
            "SQLSERVER" or "SQL SERVER" or "MSSQL" or "SQL" => "SQL Server",
            "SQLITE" => "SQLite",
            _ => provider
        };
}
