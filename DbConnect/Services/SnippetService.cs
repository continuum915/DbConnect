using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class SnippetService
{
    private readonly string _dataDir;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SnippetService(string? dataDir = null)
    {
        _dataDir = dataDir ?? Path.Combine(AppDataPaths.RootDirectory, "snippets");
    }

    private void EnsureDirectory() => Directory.CreateDirectory(_dataDir);

    private static string SanitizeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private string GlobalFilePath => Path.Combine(_dataDir, "snippets.global.json");

    private string ConnectionFilePath(string connectionName)
        => Path.Combine(_dataDir, $"snippets.{SanitizeFileName(connectionName)}.json");

    private string DatabaseFilePath(string connectionName, string databaseName)
        => Path.Combine(_dataDir, $"snippets.{SanitizeFileName(connectionName)}.{SanitizeFileName(databaseName)}.json");

    private static string FilePathForSnippet(string dataDir, Snippet snippet)
    {
        var sanitize = (string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return snippet.Scope switch
        {
            SnippetScope.Connection when !string.IsNullOrEmpty(snippet.ConnectionName)
                => Path.Combine(dataDir, $"snippets.{sanitize(snippet.ConnectionName)}.json"),
            SnippetScope.Database when !string.IsNullOrEmpty(snippet.ConnectionName) && !string.IsNullOrEmpty(snippet.DatabaseName)
                => Path.Combine(dataDir, $"snippets.{sanitize(snippet.ConnectionName)}.{sanitize(snippet.DatabaseName)}.json"),
            _ => Path.Combine(dataDir, "snippets.global.json")
        };
    }

    private static async Task<List<Snippet>> ReadFileAsync(string path)
    {
        if (!File.Exists(path)) return new List<Snippet>();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<Snippet>>(json) ?? new List<Snippet>();
        }
        catch
        {
            return new List<Snippet>();
        }
    }

    private static async Task WriteFileAsync(string path, List<Snippet> snippets)
    {
        var json = JsonSerializer.Serialize(snippets, _jsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<IReadOnlyList<Snippet>> LoadAllAsync()
    {
        EnsureDirectory();
        var all = new List<Snippet>();

        // Load global file
        all.AddRange(await ReadFileAsync(GlobalFilePath));

        // Load all connection and database scoped files
        foreach (var file in Directory.GetFiles(_dataDir, "snippets.*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file); // e.g. snippets.myconn or snippets.myconn.mydb
            // Skip global (already loaded)
            if (fileName == "snippets.global") continue;
            all.AddRange(await ReadFileAsync(file));
        }

        return all.AsReadOnly();
    }

    public async Task SaveAsync(Snippet snippet)
    {
        EnsureDirectory();
        snippet.UpdatedAt = DateTimeOffset.UtcNow;
        var path = FilePathForSnippet(_dataDir, snippet);
        var snippets = await ReadFileAsync(path);
        var idx = snippets.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0)
            snippets[idx] = snippet;
        else
            snippets.Add(snippet);
        await WriteFileAsync(path, snippets);
    }

    public async Task DeleteAsync(string id)
    {
        EnsureDirectory();
        // Search all files for the snippet
        var files = new List<string> { GlobalFilePath };
        files.AddRange(Directory.GetFiles(_dataDir, "snippets.*.json")
            .Where(f => Path.GetFileNameWithoutExtension(f) != "snippets.global"));

        foreach (var file in files)
        {
            var snippets = await ReadFileAsync(file);
            var idx = snippets.FindIndex(s => s.Id == id);
            if (idx >= 0)
            {
                snippets.RemoveAt(idx);
                await WriteFileAsync(file, snippets);
                return;
            }
        }
    }

    /// <summary>Moves a snippet to the global scope (changes scope and clears connection/database).</summary>
    public async Task PromoteToGlobalAsync(string id)
    {
        EnsureDirectory();
        var files = new List<string> { GlobalFilePath };
        files.AddRange(Directory.GetFiles(_dataDir, "snippets.*.json")
            .Where(f => Path.GetFileNameWithoutExtension(f) != "snippets.global"));

        foreach (var file in files)
        {
            var snippets = await ReadFileAsync(file);
            var idx = snippets.FindIndex(s => s.Id == id);
            if (idx >= 0)
            {
                var snippet = snippets[idx];
                if (snippet.Scope == SnippetScope.Global) return; // already global

                // Remove from current file
                snippets.RemoveAt(idx);
                await WriteFileAsync(file, snippets);

                // Promote to global
                snippet.Scope = SnippetScope.Global;
                snippet.ConnectionName = null;
                snippet.DatabaseName = null;
                snippet.UpdatedAt = DateTimeOffset.UtcNow;

                var globalSnippets = await ReadFileAsync(GlobalFilePath);
                globalSnippets.Add(snippet);
                await WriteFileAsync(GlobalFilePath, globalSnippets);
                return;
            }
        }
    }
}
