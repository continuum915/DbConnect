using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class DiagramWorkspaceService
{
    private readonly string _appDataPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DiagramWorkspaceService(string? appDataPath = null)
    {
        _appDataPath = appDataPath ?? AppDataPaths.RootDirectory;
    }

    private string WorkspacePath(string connectionName, string databaseName) =>
        Path.Combine(_appDataPath, "diagrams",
            $"{SanitizeFileName(connectionName)}_{SanitizeFileName(databaseName)}.dbcdiagram");

    public async Task<DiagramWorkspace?> LoadAsync(string connectionName, string databaseName)
    {
        var path = WorkspacePath(connectionName, databaseName);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<DiagramWorkspace>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(DiagramWorkspace workspace)
    {
        var dir = Path.Combine(_appDataPath, "diagrams");
        Directory.CreateDirectory(dir);

        var path = WorkspacePath(workspace.ConnectionName, workspace.DatabaseName);
        workspace.SavedAt = DateTime.UtcNow.ToString("O");

        try
        {
            var json = JsonSerializer.Serialize(workspace, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        catch
        {
        }
    }

    private static string SanitizeFileName(string name) =>
        Regex.Replace(name, @"[^\w\-]", "_");
}
