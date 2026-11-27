using System.Linq;
using Xunit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class ConnectionGroupTests : MainWindowTestBase
{
    private MainWindowViewModel CreateVmWithConnections(params (string name, string group)[] conns)
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        foreach (var (name, group) in conns)
        {
            var info = new ConnectionInfo { Name = name, Provider = "SQLite", Group = group };
            var rootNode = new SchemaNode { Name = name, Type = SchemaNodeType.Database };
            var session = new DatabaseSession(info, new SqliteProvider());
            vm.ActiveConnections[rootNode] = info;
            vm.ActiveSessions[rootNode] = session;
            vm.ActiveSessionOptions.Add(session);
            vm.SchemaNodes.Add(rootNode);
        }
        return vm;
    }

    [Fact]
    public void RebuildGroupedNodes_ConnectionsWithGroups_CreatesGroupFolders()
    {
        var vm = CreateVmWithConnections(("Dev DB", "Development"), ("Prod DB", "Production"));
        var nodes = vm.FilteredSchemaNodes;
        Assert.True(nodes.All(n => n.Type == SchemaNodeType.ConnectionGroup));
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public void RebuildGroupedNodes_UngroupedConnections_AppearsOutsideFolders()
    {
        var vm = CreateVmWithConnections(("Local DB", ""), ("Other DB", ""));
        var nodes = vm.FilteredSchemaNodes;
        Assert.All(nodes, n => Assert.Equal(SchemaNodeType.Database, n.Type));
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public void RenameActiveGroup_UpdatesAllConnectionsInGroup()
    {
        var vm = CreateVmWithConnections(("DB1", "OldName"), ("DB2", "OldName"), ("DB3", "Other"));
        vm.RenameActiveGroup("OldName", "NewName");
        var groups = vm.ActiveConnections.Values.Select(c => c.Group ?? "").Distinct().ToList();
        Assert.DoesNotContain("OldName", groups);
        Assert.Contains("NewName", groups);
        Assert.Contains("Other", groups);
    }

    [Fact]
    public void DeleteActiveGroup_MovesConnectionsToUngrouped()
    {
        var vm = CreateVmWithConnections(("DB1", "ToDelete"), ("DB2", "ToDelete"));
        vm.DeleteActiveGroup("ToDelete");
        Assert.All(vm.ActiveConnections.Values, c => Assert.True(string.IsNullOrEmpty(c.Group)));
    }
}
