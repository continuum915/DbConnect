using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(AppDataPaths.RootDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            var json = await File.ReadAllTextAsync(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch { }
    }
}
