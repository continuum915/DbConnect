using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DbConnect.Services;

/// <summary>DbConnect's own known-hosts file. Reads fail closed; writes replace the file atomically.</summary>
public sealed class KnownHostsStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public KnownHostsStore(string? directory = null) =>
        _path = Path.Combine(directory ?? AppDataPaths.RootDirectory, "ssh-known-hosts.json");

    public SshHostKey? Find(string host, int port)
    {
        lock (Gate)
            return Read().SingleOrDefault(key => SameEndpoint(key, host, port));
    }

    /// <summary>Persists only if the current key is still the key the user reviewed (null for a new host).</summary>
    public bool Save(SshHostKey key, SshHostKey? expected)
    {
        lock (Gate)
        {
            var keys = Read();
            var current = keys.SingleOrDefault(item => SameEndpoint(item, key.Host, key.Port));
            if (current != expected)
                return current is not null && current.Algorithm == key.Algorithm && current.Sha256Fingerprint == key.Sha256Fingerprint;
            keys.RemoveAll(item => SameEndpoint(item, key.Host, key.Port));
            keys.Add(key);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(keys));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return true;
        }
    }

    private List<SshHostKey> Read()
    {
        string document;
        try { document = File.ReadAllText(_path); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        var keys = JsonSerializer.Deserialize<List<SshHostKey>>(document)
            ?? throw new InvalidDataException("The SSH known-hosts store is invalid.");
        if (keys.Any(key => key is null || string.IsNullOrWhiteSpace(key.Host) || key.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(key.Algorithm) || string.IsNullOrWhiteSpace(key.Sha256Fingerprint)))
            throw new InvalidDataException("The SSH known-hosts store contains an invalid key.");
        return keys;
    }

    private static bool SameEndpoint(SshHostKey key, string host, int port) =>
        key.Port == port && string.Equals(key.Host, host, StringComparison.OrdinalIgnoreCase);
}
