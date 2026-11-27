using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DbConnect.Services;

/// <summary>
/// The form of a script kept in query history and the slow-query log. An oversized script keeps only a
/// preview and a hash: it identifies the run, but it cannot be run again from there.
/// </summary>
public readonly record struct StoredQueryText(string Text, bool IsTruncated, int FullLength, string? Sha256)
{
    public const int CharacterLimit = 256 * 1024;
    public const int PreviewCharacters = 4096;

    public static StoredQueryText From(string sql)
    {
        if (sql.Length <= CharacterLimit)
            return new(sql, false, sql.Length, null);

        var previewLength = char.IsHighSurrogate(sql[PreviewCharacters - 1]) ? PreviewCharacters - 1 : PreviewCharacters;
        var hash = SHA256.HashData(MemoryMarshal.AsBytes(sql.AsSpan()));
        return new(sql[..previewLength], true, sql.Length, Convert.ToHexString(hash));
    }
}
