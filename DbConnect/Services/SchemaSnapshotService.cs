using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class SchemaSnapshotService
{
    private static string GetDir(string connectionName)
    {
        var dir = Path.Combine(AppDataPaths.RootDirectory, "snapshots", SanitizeName(connectionName));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public async Task<SchemaSnapshot> TakeSnapshotAsync(DatabaseSession session, string label)
    {
        var nodes = await session.GetSchemaAsync();
        var snapshot = new SchemaSnapshot
        {
            ConnectionName = session.Name,
            Label = label,
            ObjectCount = nodes.SelectMany(n => n.Children).Count(),
            SchemaJson = JsonSerializer.Serialize(nodes)
        };

        var dir = GetDir(session.Name);
        var filePath = Path.Combine(dir, $"{snapshot.Id}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot));

        // Cap at 50 snapshots
        var existing = Directory.GetFiles(dir, "*.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        foreach (var old in existing.Skip(50))
            old.Delete();

        return snapshot;
    }

    public async Task<IReadOnlyList<SchemaSnapshot>> ListSnapshotsAsync(string connectionName)
    {
        var dir = GetDir(connectionName);
        var results = new List<SchemaSnapshot>();

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(json);
                if (snapshot != null) results.Add(snapshot);
            }
            catch { /* skip corrupt files */ }
        }

        return results.OrderByDescending(s => s.Timestamp).ToList();
    }

    public async Task<SchemaSnapshot?> LoadSnapshotAsync(string id, string connectionName)
    {
        var dir = GetDir(connectionName);
        var filePath = Path.Combine(dir, $"{id}.json");
        if (!File.Exists(filePath)) return null;
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<SchemaSnapshot>(json);
    }

    public Task DeleteSnapshotAsync(string id, string connectionName)
    {
        var dir = GetDir(connectionName);
        var filePath = Path.Combine(dir, $"{id}.json");
        if (File.Exists(filePath)) File.Delete(filePath);
        return Task.CompletedTask;
    }

    public async Task ExportSnapshotAsync(SchemaSnapshot snapshot, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<SchemaSnapshot> ImportSnapshotAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<SchemaSnapshot>(json)
            ?? throw new InvalidOperationException("Failed to deserialize snapshot.");
    }
}
