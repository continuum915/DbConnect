using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Observes attempted schema-changing execution without owning its results or transaction.</summary>
internal static class SchemaChangeExecution
{
    private static readonly string[] SchemaChangingWords =
        ["CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE", "ATTACH", "DETACH", "REINDEX", "EXEC", "EXECUTE", "GRANT", "REVOKE"];
    private static readonly string[] QueryWords = ["SELECT", "WITH", "EXPLAIN"];
    private static readonly string[] OtherChangingWords = ["COMMENT", "CALL", "DO"];

    public static bool MayChangeSchema(string sql, SqlBatchDialect dialect)
    {
        var sawWord = false;
        var startsAsQuery = false;
        var hasInto = false;
        var hasOtherChange = false;
        var afterAs = false;
        // Inspect the whole executable batch: SQL Server permits later DDL without a semicolon,
        // and PostgreSQL EXPLAIN ANALYZE can execute CREATE TABLE AS. Quoted text stays excluded.
        foreach (var token in SqlTokenizer.EnumerateRanges(sql, dialect))
        {
            var text = sql.AsSpan(token.Start, token.Length);
            if (token.Kind == SqlTokenKind.Comment)
            {
                if (text.StartsWith("/*!", StringComparison.Ordinal)) return true;
                continue;
            }
            // Keyword output labels following AS are identifiers, including labels separated by comments.
            var isLabel = afterAs;
            afterAs = text.Equals("AS", StringComparison.OrdinalIgnoreCase);
            if (token.Kind != SqlTokenKind.Word || isLabel) continue;
            if (IsAny(text, SchemaChangingWords)) return true;
            if (!sawWord)
            {
                sawWord = true;
                startsAsQuery = IsAny(text, QueryWords);
            }
            hasInto |= text.Equals("INTO", StringComparison.OrdinalIgnoreCase);
            hasOtherChange |= IsAny(text, OtherChangingWords);
        }
        // Ordinary SELECT aliases can be words such as "comment". INTO is the structural SELECT hint.
        return startsAsQuery
            ? hasInto && dialect is SqlBatchDialect.PostgreSql or SqlBatchDialect.SqlServer
            : hasOtherChange;
    }

    private static bool IsAny(ReadOnlySpan<char> word, string[] candidates)
    {
        foreach (var candidate in candidates)
            if (word.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static async IAsyncEnumerable<QueryResult> ReadAsync(DatabaseSession session, IDbSession dbSession,
        string sql, QueryExecutionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var changes = MayChangeSchema(sql, session.Capabilities.BatchDialect);
        if (changes && dbSession.IsInTransaction) await session.SchemaChanges.PendingAsync(dbSession);
        try
        {
            await foreach (var result in dbSession.ExecuteQueryMultipleAsync(sql, options, cancellationToken)) yield return result;
        }
        finally
        {
            if (!dbSession.IsInTransaction)
            {
                if (session.Catalog.EndPending(dbSession) || changes)
                    await session.SchemaChanges.InvalidateAsync(MetadataScope.Root);
            }
        }
    }
}
