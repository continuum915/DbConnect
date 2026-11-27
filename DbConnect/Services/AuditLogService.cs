using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class AuditLogService
{
    private const int MaxEntries = 100_000;

    private static string GetLogPath(string connectionName)
    {
        var dir = Path.Combine(AppDataPaths.RootDirectory, "audit");
        Directory.CreateDirectory(dir);
        var safeName = string.Concat(connectionName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(dir, $"audit-{safeName}.jsonl");
    }

    public async Task AppendAsync(AuditEntry entry)
    {
        var path = GetLogPath(entry.ConnectionName);

        // Rotate if needed
        if (File.Exists(path))
        {
            var lines = await File.ReadAllLinesAsync(path);
            if (lines.Length >= MaxEntries)
                File.Move(path, path + ".old", overwrite: true);
        }

        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public async Task<IReadOnlyList<AuditEntry>> LoadEntriesAsync(string connectionName, int limit = 1000)
    {
        var path = GetLogPath(connectionName);
        if (!File.Exists(path)) return [];

        var lines = await File.ReadAllLinesAsync(path);
        var results = new List<AuditEntry>();
        foreach (var line in lines.Reverse().Take(limit))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry != null) results.Add(entry);
            }
            catch { }
        }
        return results;
    }

    public async Task<IReadOnlyList<AuditEntry>> SearchAsync(
        string connectionName,
        string? textFilter,
        DateTimeOffset? from,
        DateTimeOffset? to,
        bool? errorsOnly)
    {
        var all = await LoadEntriesAsync(connectionName, int.MaxValue);
        IEnumerable<AuditEntry> q = all;
        if (!string.IsNullOrWhiteSpace(textFilter))
            q = q.Where(e => e.QueryText.Contains(textFilter, StringComparison.OrdinalIgnoreCase)
                          || e.QueryFingerprint.Contains(textFilter, StringComparison.OrdinalIgnoreCase));
        if (from.HasValue)
            q = q.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            q = q.Where(e => e.Timestamp <= to.Value);
        if (errorsOnly == true)
            q = q.Where(e => e.IsError);
        return q.ToList();
    }

    public async Task ExportCsvAsync(IReadOnlyList<AuditEntry> entries, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Connection,Database,Provider,DurationMs,RowsAffected,IsError,QueryFingerprint,QueryText");
        foreach (var e in entries)
        {
            sb.AppendLine($"{e.Timestamp:O},{Csv(e.ConnectionName)},{Csv(e.DatabaseName)},{Csv(e.Provider)},{e.DurationMs},{e.RowsAffected},{e.IsError},{Csv(e.QueryFingerprint)},{Csv(e.QueryText)}");
        }
        await File.WriteAllTextAsync(filePath, sb.ToString());
    }

    private static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    public Task ClearLogAsync(string connectionName)
    {
        var path = GetLogPath(connectionName);
        if (File.Exists(path)) File.Delete(path);
        var oldPath = path + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        return Task.CompletedTask;
    }
}
