using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class SessionConnectionMigration
{
    public static async Task MigrateAsync(string directory, IReadOnlyList<ConnectionInfo> connections)
    {
        await MigrateFileAsync(Path.Combine(directory, "last-session.json"), "ConnectionName", connections);
        var recoveryDirectory = Path.Combine(directory, "recovery");
        if (!Directory.Exists(recoveryDirectory))
            return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(recoveryDirectory, "session-*.json"))
                await MigrateFileAsync(path, "SessionName", connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unavailable recovery directory does not invalidate connection metadata.
        }
    }

    private static async Task MigrateFileAsync(string path, string nameProperty, IReadOnlyList<ConnectionInfo> connections)
    {
        try
        {
            await MigrateFileCoreAsync(path, nameProperty, connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Session files must not prevent loading valid saved connections; retry next load.
        }
    }

    private static async Task MigrateFileCoreAsync(string path, string nameProperty, IReadOnlyList<ConnectionInfo> connections)
    {
        if (!File.Exists(path))
            return;
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path));
        if (document is not JsonObject root || root["Tabs"] is not JsonArray tabs)
            return;
        var changed = false;
        foreach (var tab in tabs.OfType<JsonObject>())
        {
            if (tab["ConnectionId"] != null || tab[nameProperty] is not JsonValue name ||
                !name.TryGetValue<string>(out var connectionName))
                continue;
            var matches = connections.Where(c => c.Name == connectionName).ToList();
            if (matches.Count != 1)
                continue;
            tab["ConnectionId"] = matches[0].Id.ToString();
            changed = true;
        }
        if (changed)
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
