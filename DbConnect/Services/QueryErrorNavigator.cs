using System;
using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

public static class QueryErrorNavigator
{
    /// <summary>Finds the error token in the editor, or returns null when the driver location is unusable.</summary>
    public static StatementSpan? FindToken(string document, string sourceSql, string submittedSql,
        int sourceOffset, QueryResult result, IReadOnlyList<int>? sourceOffsets = null)
    {
        var position = result.Error?.Position;
        if (position == null || sourceOffset < 0 || sourceOffset > document.Length ||
            sourceOffset + sourceSql.Length > document.Length ||
            !document.AsSpan(sourceOffset, sourceSql.Length).SequenceEqual(sourceSql))
            return null;

        var start = result.StatementStart;
        var sql = result.ExecutedSql;
        if (start < 0 || start + sql.Length > submittedSql.Length ||
            !submittedSql.AsSpan(start, sql.Length).SequenceEqual(sql) ||
            (sourceOffsets != null && sourceOffsets.Count != submittedSql.Length))
            return null;

        int relative;
        if (position.Value.Offset is { } offset)
        {
            if (offset < 0 || offset >= sql.Length)
                return null;
            relative = offset;
        }
        else if (position.Value.Line is { } line && line > 0)
        {
            relative = 0;
            for (var current = 1; current < line; current++)
            {
                var newline = sql.IndexOf('\n', relative);
                if (newline < 0)
                    return null;
                relative = newline + 1;
            }
            while (relative < sql.Length && (sql[relative] == ' ' || sql[relative] == '\t'))
                relative++;
            if (relative >= sql.Length)
                return null;
        }
        else
            return null;

        var executedOffset = start + relative;
        var sourceIndex = sourceOffsets == null ? executedOffset : sourceOffsets[executedOffset];
        if (sourceIndex < 0 || sourceIndex >= sourceSql.Length)
            return null;
        var length = 1;
        if (IsTokenCharacter(sourceSql[sourceIndex]))
        {
            while (sourceIndex > 0 && IsTokenCharacter(sourceSql[sourceIndex - 1]))
                sourceIndex--;
            while (sourceIndex + length < sourceSql.Length && IsTokenCharacter(sourceSql[sourceIndex + length]))
                length++;
        }
        return new StatementSpan(sourceOffset + sourceIndex, length);
    }

    private static bool IsTokenCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$' or '@' or ':' or '?';
}
