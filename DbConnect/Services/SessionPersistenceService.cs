using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Saves and restores session tabs on normal (clean) application exit/startup.
/// Distinct from SessionRecoveryService, which handles crash recovery.
/// </summary>
public class SessionPersistenceService
{
    private readonly string _sessionFilePath;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SessionPersistenceService(string? sessionDir = null)
    {
        var dir = sessionDir ?? AppDataPaths.RootDirectory;
        Directory.CreateDirectory(dir);
        _sessionFilePath = Path.Combine(dir, "last-session.json");
    }

    public async Task SaveAsync(SavedSession session)
    {
        try
        {
            var json = JsonSerializer.Serialize(session, _jsonOptions);
            await File.WriteAllTextAsync(_sessionFilePath, json);
        }
        catch
        {
            // Ignore write errors on exit.
        }
    }

    public async Task<SavedSession?> LoadAsync()
    {
        if (!File.Exists(_sessionFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_sessionFilePath);
            return JsonSerializer.Deserialize<SavedSession>(json);
        }
        catch
        {
            return null;
        }
    }

    public bool Exists() => File.Exists(_sessionFilePath);
}
