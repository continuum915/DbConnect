using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public sealed record SqlCompletionCandidate(string Text, CompletionKind Kind, string? InsertionText = null);

/// <summary>
/// RAIN-P2-24 — Decides what the SQL editor offers at the caret: keywords, tables, columns, the provider's
/// functions, schemas, and after a qualifier (<c>alias.</c>, <c>schema.</c>, <c>schema.table.</c>) only that
/// qualifier's members.
/// </summary>
public static class SqlCompletionEngine
{
    private static readonly char[] WordSeparators = [' ', '\n', '\r', '\t', ',', '(', ')'];

    private enum SqlContext { Unknown, Table, Column }

    /// <summary>Where the word being completed starts: the caret, walked back over identifier characters.</summary>
    public static int WordStart(string text, int caret)
    {
        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;
        return start;
    }

    /// <summary>The schema named by the caret's qualifier when its objects haven't been fetched yet, else null.</summary>
    public static string? SchemaToLoad(string text, int caret, SchemaIndex index)
    {
        if (Qualifier(text, caret) is not [var schema])
            return null;

        var known = index.SchemaObjects.Keys.FirstOrDefault(k => string.Equals(k, schema, StringComparison.OrdinalIgnoreCase));
        return known != null && !index.LoadedSchemas.Contains(known) ? known : null;
    }

    public static IReadOnlyList<SqlCompletionCandidate> GetCandidates(string text, int caret, SchemaIndex index)
    {
        var qualifier = Qualifier(text, caret);
        var candidates = qualifier != null
            ? QualifiedMembers(text, caret, qualifier, index)
            : Unqualified(text[..caret], index);
        return candidates.Distinct().ToList();
    }

    private static IEnumerable<SqlCompletionCandidate> QualifiedMembers(string text, int caret, string[] qualifier, SchemaIndex index)
    {
        if (qualifier.Length == 0)
            yield break;

        var table = string.Join('.', qualifier);
        if (qualifier.Length == 1 && SqlAliasScope.AliasesAt(text, caret).TryGetValue(table, out var aliased))
            table = aliased;

        if (index.TableColumns.TryGetValue(table, out var columns))
        {
            foreach (var column in columns)
                yield return new(column, CompletionKind.Column);
        }

        if (qualifier.Length == 1 && index.SchemaObjects.TryGetValue(qualifier[0], out var objects))
        {
            foreach (var (name, type) in objects)
                yield return new(name, KindOf(type));
        }
    }

    private static IEnumerable<SqlCompletionCandidate> Unqualified(string textBeforeCaret, SchemaIndex index)
    {
        var candidates = SqlKeywordProvider.GetKeywords(textBeforeCaret, index.Provider)
            .Select(k => new SqlCompletionCandidate(k, CompletionKind.Keyword))
            .ToList();

        var tables = index.Tables.Select(t => new SqlCompletionCandidate(t, CompletionKind.Table));
        var columns = index.AllColumns.Select(c => new SqlCompletionCandidate(c, CompletionKind.Column));
        var functions = index.Functions.Select(f => new SqlCompletionCandidate(f, CompletionKind.Function));
        var schemas = index.SchemaObjects.Keys.Select(s => new SqlCompletionCandidate(s, CompletionKind.Schema));

        switch (GetContext(textBeforeCaret))
        {
            case SqlContext.Table:
                candidates.AddRange(tables.Concat(schemas));
                break;
            case SqlContext.Column:
                candidates.AddRange(columns.Concat(tables).Concat(functions));
                break;
            default:
                candidates.AddRange(tables.Concat(columns).Concat(functions).Concat(schemas));
                break;
        }

        return candidates;
    }

    /// <summary>
    /// The dotted identifier before the word at the caret, quotes stripped: null when the word isn't
    /// qualified, empty when the qualifier isn't an identifier (a bare ".").
    /// </summary>
    private static string[]? Qualifier(string text, int caret)
    {
        var dot = WordStart(text, caret) - 1;
        if (dot < 0 || text[dot] != '.')
            return null;

        var start = dot;
        while (start > 0 && (IsWordChar(text[start - 1]) || text[start - 1] is '.' or '"' or '`' or '[' or ']'))
            start--;

        var segments = text[start..dot].Split('.').Select(s => s.Trim('"', '`', '[', ']')).ToArray();
        return segments.Any(string.IsNullOrEmpty) ? [] : segments;
    }

    internal static bool ExpectsRelation(string text) => GetContext(text) == SqlContext.Table;

    private static SqlContext GetContext(string textBeforeCaret)
    {
        var words = textBeforeCaret.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

        for (int i = words.Length - 1; i >= 0; i--)
        {
            switch (words[i].ToUpperInvariant())
            {
                case "FROM":
                case "JOIN":
                case "INTO":
                case "UPDATE":
                    return SqlContext.Table;
                case "SELECT":
                case "WHERE":
                case "AND":
                case "OR":
                case "ON":
                case "SET":
                case "GROUP":
                case "ORDER":
                case "HAVING":
                    return SqlContext.Column;
            }
        }
        return SqlContext.Unknown;
    }

    private static CompletionKind KindOf(SchemaNodeType type) => type switch
    {
        SchemaNodeType.View or SchemaNodeType.MaterializedView => CompletionKind.View,
        SchemaNodeType.Function => CompletionKind.Function,
        _ => CompletionKind.Table
    };

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
