using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class ConnectionGroupActionsViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-group-actions-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameOrDelete_UpdatesActiveAndSavedConnections(bool delete)
    {
        var settings = new ConnectionSettingsService(new MemoryStore(), _directory);
        var active = new ConnectionInfo { Name = "Active", Provider = "SQLite", Group = "Old" };
        var offline = new ConnectionInfo { Name = "Offline", Provider = "SQLite", Group = "Old" };
        var other = new ConnectionInfo { Name = "Other", Provider = "SQLite", Group = "Other" };
        await settings.SaveConnectionsAsync([active, offline, other]);
        var connections = new Dictionary<SchemaNode, ConnectionInfo>();
        var tree = new SchemaTreeViewModel(connections, new(), new());
        var node = new SchemaNode { Name = active.Name, Type = SchemaNodeType.Database };
        connections[node] = active;
        tree.SchemaNodes.Add(node);
        var actions = new ConnectionGroupActions(tree, connections, settings);

        if (delete)
            await actions.DeleteAsync("Old");
        else
            await actions.RenameAsync("Old", "New");

        var expected = delete ? string.Empty : "New";
        Assert.Equal(expected, active.Group);
        var saved = await settings.LoadConnectionsAsync();
        Assert.Equal(expected, saved.Single(c => c.Name == "Active").Group);
        Assert.Equal(expected, saved.Single(c => c.Name == "Offline").Group);
        Assert.Equal("Other", saved.Single(c => c.Name == "Other").Group);
        Assert.Equal(delete ? SchemaNodeType.Database : SchemaNodeType.ConnectionGroup, tree.FilteredSchemaNodes[0].Type);
    }

    [Fact]
    public async Task MoveFilteredNode_UpdatesActiveAndSavedGroup()
    {
        var settings = new ConnectionSettingsService(new MemoryStore(), _directory);
        var connection = new ConnectionInfo { Name = "Active", Provider = "SQLite", Group = "Old" };
        await settings.SaveConnectionsAsync([connection]);
        var connections = new Dictionary<SchemaNode, ConnectionInfo>();
        var tree = new SchemaTreeViewModel(connections, new(), new());
        var node = new SchemaNode { Name = connection.Name, Type = SchemaNodeType.Database };
        connections[node] = connection;
        tree.SchemaNodes.Add(node);
        var actions = new ConnectionGroupActions(tree, connections, settings);

        await actions.MoveAsync(new SchemaNode { RealNode = node }, "New");

        Assert.Equal("New", connection.Group);
        Assert.Equal("New", Assert.Single(await settings.LoadConnectionsAsync()).Group);
        Assert.Equal(["New"], actions.AvailableGroups);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class MemoryStore : ICredentialStore
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
