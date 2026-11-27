using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Appends slow query entries to per-connection JSONL log files under %AppData%/DbConnect/.
/// </summary>
public class SlowQueryLogService
{
    private static readonly Lazy<SlowQueryLogService> _default = new(() => new SlowQueryLogService());
    public static SlowQueryLogService Default => _default.Value;

    public int SlowQueryThresholdMs { get; set; } = 1000;

    private const int MaxEntries = 10_000;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task AppendAsync(SlowQueryEntry entry)
    {
        var path = GetLogPath(entry.ConnectionName);
        EnsureDirectory(path);

        // Rotate if needed
        if (File.Exists(path))
        {
            var lineCount = CountLines(path);
            if (lineCount >= MaxEntries)
            {
                var oldPath = path + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(path, oldPath);
            }
        }

        var line = JsonSerializer.Serialize(entry, _json);
        await File.AppendAllTextAsync(path, line + Environment.NewLine);
    }

    /// <summary>Records a slow run. Hashing or fingerprinting a large script is slow, so this all happens off the caller's thread.</summary>
    public Task LogExecutedAsync(string connectionName, TimeSpan duration, int rowsAffected, bool isError, string sql) => Task.Run(() =>
    {
        var stored = StoredQueryText.From(sql);
        return AppendAsync(new SlowQueryEntry
        {
            ConnectionName = connectionName,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration,
            RowsAffected = rowsAffected,
            IsError = isError,
            QueryText = stored.Text,
            IsTruncated = stored.IsTruncated,
            Fingerprint = stored.IsTruncated ? $"oversized:{stored.Sha256}" : Fingerprint(sql)
        });
    });

    public async Task<IReadOnlyList<SlowQueryEntry>> LoadEntriesAsync(string connectionName)
    {
        var path = GetLogPath(connectionName);
        if (!File.Exists(path)) return Array.Empty<SlowQueryEntry>();

        var lines = await File.ReadAllLinesAsync(path);
        var entries = new List<SlowQueryEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<SlowQueryEntry>(line, _json);
                if (entry != null) entries.Add(entry);
            }
            catch { /* skip malformed lines */ }
        }
        return entries;
    }

    public async Task<IReadOnlyList<SlowQueryAggregate>> GetAggregatesAsync(string connectionName)
    {
        var entries = await LoadEntriesAsync(connectionName);
        return entries
            .GroupBy(e => e.Fingerprint)
            .Select(g => new SlowQueryAggregate
            {
                Fingerprint = g.Key,
                SampleQuery = g.First().QueryText,
                IsTruncated = g.First().IsTruncated,
                Count = g.Count(),
                AvgDuration = TimeSpan.FromTicks((long)g.Average(e => e.Duration.Ticks)),
                MaxDuration = g.Max(e => e.Duration),
                LastSeen = g.Max(e => e.Timestamp),
                ConnectionName = connectionName
            })
            .OrderByDescending(a => a.MaxDuration)
            .ToList();
    }

    public async Task ClearAsync(string connectionName)
    {
        var path = GetLogPath(connectionName);
        if (File.Exists(path)) File.Delete(path);
        var oldPath = path + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Strips string and number literals, normalizes whitespace, lowercases.
    /// </summary>
    public string Fingerprint(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return string.Empty;

        // Strip string literals ('...')
        var result = Regex.Replace(sql, @"'[^']*'", "?");
        // Strip number literals (standalone digits, optionally decimal)
        result = Regex.Replace(result, @"\b\d+(?:\.\d+)?\b", "?");
        // Normalize whitespace
        result = Regex.Replace(result, @"\s+", " ").Trim();
        return result.ToLowerInvariant();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetLogPath(string connectionName)
    {
        var safe = Regex.Replace(connectionName ?? "default", @"[^\w\-]", "_");
        return Path.Combine(AppDataPaths.RootDirectory, $"slow-queries-{safe}.jsonl");
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static int CountLines(string path)
    {
        int count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() != null) count++;
        return count;
    }
}
