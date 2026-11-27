using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

public static class SqlKeywordProvider
{
    private static readonly string[] CommonKeywords = {
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "UPDATE", "SET", "DELETE", "JOIN",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "ON", "AND", "OR", "NOT", "IN", "IS", "NULL",
        "LIKE", "ORDER BY", "GROUP BY", "HAVING", "LIMIT", "OFFSET",
        "ASC", "DESC", "CREATE", "ALTER", "DROP", "VALUES"
    };

    private static readonly string[] SqliteSpecificKeywords = {
        "GLOB", "MATCH", "REGEXP", "PRAGMA", "WITHOUT ROWID", "STRICT", "VIRTUAL TABLE",
        "USING", "ATTACH", "DETACH"
    };

    private static readonly Dictionary<string, string[]> ProviderKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PostgreSQL"] = new[]
        {
            "RETURNING", "ON CONFLICT", "ILIKE", "LATERAL", "TABLESAMPLE", "WITHIN GROUP",
            "FILTER", "EXCLUDE", "NULLS FIRST", "NULLS LAST", "GENERATED ALWAYS AS",
            "STORED", "PARTITION BY", "OVER", "MATERIALIZED", "REFRESH", "CONCURRENTLY",
            "UNLOGGED", "VACUUM", "ANALYZE", "EXPLAIN ANALYZE", "COPY", "RETURNING"
        },
        ["SQL Server"] = new[]
        {
            "TOP", "NOLOCK", "WITH (NOLOCK)", "OUTPUT", "MERGE", "INSERTED", "DELETED",
            "IDENTITY", "NEWID", "GETDATE", "GETUTCDATE", "COALESCE", "ISNULL", "IIF",
            "TRY_CAST", "TRY_CONVERT", "STRING_AGG", "OPENJSON", "FOR JSON", "FOR XML",
            "ROWNUM", "GO", "USE", "EXEC", "EXECUTE", "DECLARE", "BEGIN TRAN", "COMMIT TRAN",
            "ROLLBACK TRAN", "PRINT"
        },
        ["MySQL"] = new[]
        {
            "REPLACE INTO", "ON DUPLICATE KEY UPDATE", "STRAIGHT_JOIN",
            "SQL_CALC_FOUND_ROWS", "FOUND_ROWS", "GROUP_CONCAT", "JSON_EXTRACT",
            "JSON_OBJECT", "IFNULL", "CONVERT", "COLLATE", "ENGINE", "CHARSET",
            "AUTO_INCREMENT", "SHOW TABLES", "SHOW DATABASES", "SHOW CREATE TABLE",
            "DESCRIBE", "EXPLAIN"
        },
        ["SQLite"] = new[]
        {
            "WITHOUT ROWID", "STRICT", "VIRTUAL TABLE", "USING", "MATCH", "GLOB",
            "PRAGMA", "ATTACH", "DETACH", "VACUUM", "REINDEX", "ANALYZE"
        },
        ["DuckDB"] = new[]
        {
            "SAMPLE", "USING SAMPLE", "PIVOT", "UNPIVOT", "INSTALL", "LOAD",
            "ATTACH", "DETACH", "COPY", "SUMMARIZE", "DESCRIBE", "SHOW TABLES",
            "QUALIFY", "ASOF", "EXCLUDE", "REPLACE"
        }
    };

    private static readonly Dictionary<string, string[]> ProviderSnippets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PostgreSQL"] = new[]
        {
            "ON CONFLICT (${1:column}) DO NOTHING",
            "ON CONFLICT (${1:column}) DO UPDATE SET ${2:col} = EXCLUDED.${2:col}",
            "INSERT INTO ${1:table} (${2:cols}) VALUES (${3:vals}) RETURNING *",
            "SELECT $1 FROM $2 WHERE $3 LIMIT 100 OFFSET 0",
        },
        ["SQL Server"] = new[]
        {
            "SELECT TOP 100 * FROM ${1:table}",
            "WITH ${1:cte} AS (\\n\\t${2:query}\\n)\\nSELECT * FROM ${1:cte}",
            "SELECT * FROM ${1:table} WITH (NOLOCK)",
        },
        ["MySQL"] = new[]
        {
            "REPLACE INTO ${1:table} (${2:cols}) VALUES (${3:vals})",
            "INSERT INTO ${1:table} (${2:cols}) VALUES (${3:vals}) ON DUPLICATE KEY UPDATE ${4:col} = VALUES(${4:col})",
            "SELECT GROUP_CONCAT(${1:col} ORDER BY ${2:col} SEPARATOR ', ') FROM ${3:table}",
        },
        ["SQLite"] = new[]
        {
            "CREATE TABLE ${1:name} (${2:cols}) WITHOUT ROWID",
            "PRAGMA table_info('${1:table}')",
            "PRAGMA foreign_key_list('${1:table}')",
        },
        ["DuckDB"] = new[]
        {
            "SUMMARIZE ${1:table}",
            "PIVOT ${1:table} ON ${2:col} USING SUM(${3:val})",
            "COPY (${1:query}) TO '${2:file.parquet}'",
        }
    };

    public static List<string> GetKeywords(string textBeforeCaret, string? provider)
    {
        var words = textBeforeCaret.Split(new[] { ' ', '\n', '\r', '\t', ',', '(', ')', ';' }, StringSplitOptions.RemoveEmptyEntries);
        bool endsWithDelimiter = textBeforeCaret.Length > 0 && char.IsWhiteSpace(textBeforeCaret[^1]);

        var previousWords = endsWithDelimiter
            ? words
            : words.Length > 1 ? words.Take(words.Length - 1).ToArray() : Array.Empty<string>();

        if (previousWords.Length == 0)
        {
            var startKeywords = new List<string> { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER" };
            if (provider == "SQLite") startKeywords.Add("PRAGMA");
            if (provider == "DuckDB") { startKeywords.Add("SUMMARIZE"); startKeywords.Add("PIVOT"); }
            return startKeywords;
        }

        var lastSignificant = GetLastSignificantKeyword(previousWords);
        var suggestions = new List<string>();

        switch (lastSignificant)
        {
            case "SELECT":
                suggestions.Add("FROM");
                break;
            case "FROM":
            case "JOIN":
                suggestions.AddRange(new[] { "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "ON", "GROUP BY", "ORDER BY", "LIMIT" });
                break;
            case "WHERE":
            case "ON":
            case "HAVING":
                suggestions.AddRange(new[] { "AND", "OR", "IN", "IS", "NOT", "LIKE", "GROUP BY", "ORDER BY", "LIMIT" });
                if (provider == "SQLite") suggestions.AddRange(new[] { "GLOB", "MATCH", "REGEXP" });
                if (provider == "PostgreSQL") suggestions.Add("ILIKE");
                break;
            case "GROUP":
            case "GROUP BY":
                suggestions.AddRange(new[] { "HAVING", "ORDER BY", "LIMIT" });
                break;
            case "ORDER":
            case "ORDER BY":
                suggestions.AddRange(new[] { "ASC", "DESC", "LIMIT" });
                break;
            case "INSERT":
                suggestions.Add("INTO");
                break;
            case "INTO":
                suggestions.Add("VALUES");
                if (provider == "PostgreSQL") suggestions.Add("RETURNING");
                break;
            case "VALUES":
                if (provider == "PostgreSQL") suggestions.Add("ON CONFLICT");
                if (provider == "MySQL") suggestions.Add("ON DUPLICATE KEY UPDATE");
                break;
            case "UPDATE":
                suggestions.Add("SET");
                break;
            case "SET":
                suggestions.Add("WHERE");
                break;
            default:
                suggestions.AddRange(CommonKeywords);
                if (provider == "SQLite") suggestions.AddRange(SqliteSpecificKeywords);
                if (provider != null && ProviderKeywords.TryGetValue(provider, out var provKws))
                    suggestions.AddRange(provKws);
                break;
        }

        return suggestions.Distinct().ToList();
    }

    public static IReadOnlyList<string> GetSnippets(string? provider)
    {
        if (provider == null) return Array.Empty<string>();
        return ProviderSnippets.TryGetValue(provider, out var snippets) ? snippets : Array.Empty<string>();
    }

    private static string GetLastSignificantKeyword(string[] words)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "SELECT", "FROM", "WHERE", "JOIN", "ON", "GROUP", "ORDER", "BY", "HAVING", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE"
        };

        for (int i = words.Length - 1; i >= 0; i--)
        {
            var upper = words[i].ToUpperInvariant();
            if (keywords.Contains(upper))
            {
                if (upper == "BY")
                {
                    if (i > 0 && (words[i - 1].ToUpperInvariant() == "GROUP" || words[i - 1].ToUpperInvariant() == "ORDER"))
                        return words[i - 1].ToUpperInvariant() + " BY";
                }
                return upper;
            }
        }
        return "UNKNOWN";
    }
}
