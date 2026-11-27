using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Edits connection groups in the explorer and saved connection metadata.</summary>
public sealed class ConnectionGroupActions
{
    private readonly SchemaTreeViewModel _tree;
    private readonly Dictionary<SchemaNode, ConnectionInfo> _connections;
    private ConnectionSettingsService? _settings;

    public ConnectionGroupActions(SchemaTreeViewModel tree,
        Dictionary<SchemaNode, ConnectionInfo> connections, ConnectionSettingsService? settings = null)
    {
        _tree = tree;
        _connections = connections;
        _settings = settings;
    }

    private ConnectionSettingsService Settings => _settings ??= new ConnectionSettingsService();

    public List<string> AvailableGroups => _connections.Values
        .Select(c => c.Group ?? string.Empty)
        .Where(g => !string.IsNullOrEmpty(g))
        .Distinct()
        .OrderBy(g => g)
        .ToList();

    public async Task RenameAsync(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || oldName == newName)
            return;

        _tree.RenameActiveGroup(oldName, newName);
        await UpdateSavedGroupAsync(oldName, newName);
    }

    public async Task DeleteAsync(string name)
    {
        _tree.DeleteActiveGroup(name);
        await UpdateSavedGroupAsync(name, string.Empty);
    }

    public async Task MoveAsync(SchemaNode node, string group)
    {
        _tree.MoveConnectionToGroup(node, group);
        var root = node.RealNode ?? node;
        if (!_connections.TryGetValue(root, out var connection))
            return;

        var all = await Settings.LoadConnectionsAsync();
        var saved = all.FirstOrDefault(c => c.Name == connection.Name);
        if (saved != null)
            saved.Group = group;
        await Settings.SaveConnectionsAsync(all);
    }

    private async Task UpdateSavedGroupAsync(string oldName, string newName)
    {
        var all = await Settings.LoadConnectionsAsync();
        foreach (var connection in all)
            if ((connection.Group ?? string.Empty) == oldName)
                connection.Group = newName;
        await Settings.SaveConnectionsAsync(all);
    }
}
