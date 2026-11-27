using System;
using System.Threading;

namespace DbConnect.Services;

internal static class SqlScanCancellation
{
    internal static void Check(CancellationToken token, int position)
    {
        if ((position & 1023) == 0) token.ThrowIfCancellationRequested();
    }

    internal static bool IsWhiteSpace(string sql, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (var i = 0; i < sql.Length; i++)
        {
            Check(token, i);
            if (!char.IsWhiteSpace(sql[i])) return false;
        }
        return true;
    }

    internal static int IndexOf(string sql, string value, int start, CancellationToken token)
    {
        const int chunk = 4096;
        for (var i = start; i <= sql.Length - value.Length; i += chunk)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(sql.Length - i, chunk + value.Length - 1);
            var found = sql.AsSpan(i, length).IndexOf(value, StringComparison.Ordinal);
            if (found >= 0) return i + found;
        }
        return -1;
    }
}
