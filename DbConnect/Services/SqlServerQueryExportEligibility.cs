using System;
using System.Collections.Generic;

namespace DbConnect.Services;

internal static class SqlServerQueryExportEligibility
{
    private static readonly HashSet<string> ExcludedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE", "SET", "DECLARE",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "BEGIN", "COMMIT", "ROLLBACK", "SAVE",
        "WAITFOR", "DBCC", "USE", "GRANT", "DENY", "REVOKE", "BACKUP", "RESTORE",
        "KILL", "SHUTDOWN", "RECONFIGURE", "PRINT", "RAISERROR", "THROW", "RETURN",
        "IF", "WHILE", "GOTO", "OPEN", "CLOSE", "DEALLOCATE", "FETCH", "RECEIVE", "SEND",
        "CHECKPOINT", "BULK", "ENABLE", "DISABLE", "END", "HOLDLOCK", "UPDLOCK", "XLOCK",
        "TABLOCK", "TABLOCKX", "SERIALIZABLE", "REPEATABLEREAD", "SETUSER", "REVERT",
        "WRITETEXT", "UPDATETEXT", "READTEXT", "DUMP", "LOAD", "MOVE", "GET"
    };

    public static bool IsRead(IReadOnlyList<SqlToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.Word && ExcludedWords.Contains(tokens[i].Text)) return false;
            if (i > 0 && tokens[i].Kind == SqlTokenKind.Word && tokens[i].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                && tokens[i - 1].Text != "(" && !tokens[i - 1].Text.Equals("UNION", StringComparison.OrdinalIgnoreCase)
                && !tokens[i - 1].Text.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                && !tokens[i - 1].Text.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase)
                && !tokens[i - 1].Text.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)) return false;
            if (tokens[i].Text.StartsWith("@", StringComparison.Ordinal) && i + 1 < tokens.Count
                && tokens[i + 1].Text is "=" or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^") return false;
        }
        return true;
    }
}
