using System;

namespace DbConnect.Models;

public sealed class QueryHistoryEntry
{
    public string Sql { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
    public QueryExecutionScope Scope { get; set; }
    /// <summary>True when <see cref="Sql"/> is only a preview of an oversized script, so it must not be run.</summary>
    public bool IsTruncated { get; set; }
    public int FullLength { get; set; }
    public string? Sha256 { get; set; }

    public string DisplayName
    {
        get
        {
            var compactSql = Sql.Replace("\r", " ").Replace("\n", " ").Trim();
            if (compactSql.Length > 60)
                compactSql = compactSql[..60] + "...";

            var note = IsTruncated ? $"[{FullLength / (1024d * 1024d):0.#} MiB script, preview only] " : string.Empty;
            return $"{ExecutedAt.LocalDateTime:g} - {note}{compactSql}";
        }
    }
}
