using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// Windows credential store backed by DPAPI (ProtectedData, CurrentUser scope).
/// Secrets are stored as individual encrypted entries in a JSON vault dictionary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly string _vaultPath;

    public WindowsCredentialStore()
    {
        var dir = AppDataPaths.RootDirectory;
        Directory.CreateDirectory(dir);
        _vaultPath = Path.Combine(dir, "secrets.dpapi.json");
    }

    public async Task SetAsync(string key, string secret)
    {
        var vault = await LoadVaultAsync();
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        vault[key] = Convert.ToBase64String(encrypted);
        await SaveVaultAsync(vault);
    }

    public async Task<string?> GetAsync(string key)
    {
        var vault = await LoadVaultAsync();
        if (!vault.TryGetValue(key, out var b64)) return null;
        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return null; }
    }

    public async Task DeleteAsync(string key)
    {
        var vault = await LoadVaultAsync();
        if (vault.Remove(key))
            await SaveVaultAsync(vault);
    }

    private async Task<Dictionary<string, string>> LoadVaultAsync()
    {
        if (!File.Exists(_vaultPath)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(_vaultPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    private async Task SaveVaultAsync(Dictionary<string, string> vault)
    {
        var json = JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_vaultPath, json);
    }
}
