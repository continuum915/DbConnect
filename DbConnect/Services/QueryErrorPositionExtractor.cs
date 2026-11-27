using System;
using System.Text.RegularExpressions;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Converts driver locations to positions within the SQL sent for one statement.</summary>
public static class QueryErrorPositionExtractor
{
    public static QueryErrorPosition? PostgreSql(int position) =>
        position > 0 ? new QueryErrorPosition(Offset: position - 1) : null;

    public static QueryErrorPosition? SqlServer(int line) =>
        line > 0 ? new QueryErrorPosition(Line: line) : null;

    public static QueryErrorPosition? MySql(string message)
    {
        var match = Regex.Match(message, @"\bat line (\d+)\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var line) && line > 0
            ? new QueryErrorPosition(Line: line)
            : null;
    }

    public static QueryErrorPosition? Sqlite(string message, string sql)
    {
        var match = Regex.Match(message, "near [\"'`](.+?)[\"'`]:", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var offset = sql.IndexOf(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        return offset >= 0 ? new QueryErrorPosition(Offset: offset) : null;
    }
}
