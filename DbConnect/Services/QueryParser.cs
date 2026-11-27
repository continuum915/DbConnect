using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DbConnect.Services;

public static class QueryParser
{
    // Matches: FROM TableName alias  or  FROM TableName AS alias
    private static readonly Regex _fromAlias = new(
        @"\bFROM\s+(\w+)\s+(?:AS\s+)?(\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches: JOIN TableName alias  or  JOIN TableName AS alias
    private static readonly Regex _joinAlias = new(
        @"\b(?:INNER\s+|LEFT\s+|RIGHT\s+|FULL\s+|CROSS\s+)?JOIN\s+(\w+)\s+(?:AS\s+)?(\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "AND", "OR", "SET", "GROUP", "ORDER", "HAVING", "LIMIT", "INNER",
        "LEFT", "RIGHT", "FULL", "CROSS", "JOIN", "SELECT", "FROM", "AS", "INTO", "UPDATE"
    };

    /// <summary>
    /// Parses all alias → table mappings in the given SQL text.
    /// Returns a case-insensitive dictionary: alias → table_name.
    /// </summary>
    public static Dictionary<string, string> ParseAliasMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in _fromAlias.Matches(sql))
        {
            var table = m.Groups[1].Value;
            var alias = m.Groups[2].Value;
            // Don't register SQL keywords as aliases
            if (!SqlKeywords.Contains(alias))
                map[alias] = table;
        }

        foreach (Match m in _joinAlias.Matches(sql))
        {
            var table = m.Groups[1].Value;
            var alias = m.Groups[2].Value;
            if (!SqlKeywords.Contains(alias))
                map[alias] = table;
        }

        return map;
    }
}
