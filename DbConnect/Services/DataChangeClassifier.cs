using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DbConnect.Services;

/// <summary>
/// Decides whether SQL might change data, for confirmations before running it on sensitive
/// connections. It errs on the side of asking: only statements known to be read-only pass, and
/// anything unrecognised counts as a change. This is not a parser and not a security boundary.
/// </summary>
public static class DataChangeClassifier
{
    private static readonly HashSet<string> AlwaysReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALUES", "TABLE", "SHOW", "DESCRIBE", "DESC", "USE",
        // Transaction control changes no data itself; the statements inside the transaction are checked.
        "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "END"
    };

    private static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "TRUNCATE",
        "CREATE", "DROP", "ALTER", "INTO", "COPY", "CALL", "EXEC", "EXECUTE", "GRANT", "REVOKE"
    };

    /// <summary>The first statement in <paramref name="sql"/> that might change data, or null when all are read-only.</summary>
    public static string? FirstDataChange(string sql, SqlBatchDialect dialect, CancellationToken cancellationToken = default) =>
        SqlBatchParser.SplitStatements(sql, dialect, cancellationToken)
            .FirstOrDefault(statement => !IsReadOnly(statement, dialect))?.Trim();

    public static bool IsReadOnly(string statement, SqlBatchDialect dialect)
    {
        var tokens = SqlTokenizer.Tokenize(statement, dialect)
            .Where(t => t.Kind is not SqlTokenKind.Comment)
            .SkipWhile(t => t.Kind == SqlTokenKind.Symbol && t.Text is "(" or ";")
            .ToList();
        if (tokens.Count == 0) return true;
        if (tokens[0].Kind != SqlTokenKind.Word) return false;

        var first = tokens[0].Text;
        if (AlwaysReadOnly.Contains(first)) return true;
        if (Is(first, "SELECT"))
            return !HasWord(tokens, "INTO"); // SELECT … INTO creates a table or writes a file
        if (Is(first, "WITH"))
            return !tokens.Any(t => t.Kind == SqlTokenKind.Word && Writes.Contains(t.Text)); // writable CTEs
        if (Is(first, "EXPLAIN"))
            return !HasWord(tokens, "ANALYZE") && !HasWord(tokens, "ANALYSE") || IsReadOnly(InnerStatement(statement, tokens), dialect);
        if (Is(first, "PRAGMA"))
            return !tokens.Any(t => t.Kind == SqlTokenKind.Symbol && t.Text == "="); // PRAGMA name = value changes settings
        if (Is(first, "SET"))
            return !HasWord(tokens, "GLOBAL") && !HasWord(tokens, "PERSIST"); // session settings only
        return false;
    }

    /// <summary>The statement EXPLAIN ANALYZE runs: from its first SELECT, WITH or write keyword.</summary>
    private static string InnerStatement(string statement, IReadOnlyList<SqlToken> tokens)
    {
        var inner = tokens.Skip(1).FirstOrDefault(t => t.Kind == SqlTokenKind.Word &&
            (Is(t.Text, "SELECT") || Is(t.Text, "WITH") || Is(t.Text, "VALUES") || Writes.Contains(t.Text)));
        return inner.Text == null ? "UNKNOWN" : statement[inner.Start..];
    }

    private static bool HasWord(IEnumerable<SqlToken> tokens, string word) =>
        tokens.Any(t => t.Kind == SqlTokenKind.Word && Is(t.Text, word));

    private static bool Is(string text, string word) => text.Equals(word, StringComparison.OrdinalIgnoreCase);
}
