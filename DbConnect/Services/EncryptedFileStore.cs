using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// Cross-platform credential store using AES-256-GCM encryption with a PBKDF2-derived key
/// based on machine name and user name.  Used on macOS, Linux, and as a fallback everywhere.
/// </summary>
public sealed class EncryptedFileStore : ICredentialStore
{
    // Kept from the app's former name: it is a key-derivation constant, and changing it would make existing vaults unreadable.
    private static readonly byte[] Salt =
        Encoding.UTF8.GetBytes("RainSqlEditor-v1-credential-salt-2026");

    private readonly string _vaultPath;
    private readonly byte[] _key;

    public EncryptedFileStore()
        : this(ResolveVaultDir(), $"{Environment.MachineName}\0{Environment.UserName}")
    {
    }

    /// <summary>Opens a vault using an explicit directory and key-derivation identity.</summary>
    public EncryptedFileStore(string directory, string identity)
    {
        Directory.CreateDirectory(directory);
        _vaultPath = Path.Combine(directory, "vault.json");
        _key = DeriveKey(identity);
    }

    public async Task SetAsync(string key, string secret)
    {
        var vault = await LoadVaultAsync();
        vault[key] = Encrypt(secret);
        await SaveVaultAsync(vault);
    }

    public async Task<string?> GetAsync(string key)
    {
        var vault = await LoadVaultAsync();
        if (!vault.TryGetValue(key, out var ciphertext)) return null;
        try { return Decrypt(ciphertext); }
        catch { return null; }
    }

    public async Task DeleteAsync(string key)
    {
        var vault = await LoadVaultAsync();
        if (vault.Remove(key))
            await SaveVaultAsync(vault);
    }

    // ── Encryption helpers ────────────────────────────────────────────────────

    private string Encrypt(string plaintext)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: [12-byte nonce][ciphertext][16-byte tag]
        var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(combined, 0);
        ciphertext.CopyTo(combined, nonce.Length);
        tag.CopyTo(combined, nonce.Length + ciphertext.Length);

        return Convert.ToBase64String(combined);
    }

    private string Decrypt(string base64)
    {
        var combined = Convert.FromBase64String(base64);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = combined[..nonceSize];
        var ciphertext = combined[nonceSize..(combined.Length - tagSize)];
        var tag = combined[(combined.Length - tagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string identity)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            identity, Salt, iterations: 100_000, HashAlgorithmName.SHA256, outputLength: 32);
    }

    // ── Platform-specific vault directory ─────────────────────────────────────

    private static string ResolveVaultDir() => AppDataPaths.CredentialDirectory;

    // ── Vault persistence ─────────────────────────────────────────────────────

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

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Returns the best <see cref="ICredentialStore"/> for the current platform.</summary>
    public static ICredentialStore CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        return new EncryptedFileStore(); // macOS + Linux
    }
}
