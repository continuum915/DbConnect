using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class SchemaTreeViewModelTests
{
    [AvaloniaFact]
    public void GroupsSortConnectionsAndKeepUngroupedConnectionsLast()
    {
        var connections = new Dictionary<SchemaNode, ConnectionInfo>();
        var tree = new SchemaTreeViewModel(connections, new(), new());
        var zulu = AddConnection(tree, connections, "Zulu", "Development");
        var alpha = AddConnection(tree, connections, "Alpha", "Development");
        var local = AddConnection(tree, connections, "Local", "");

        Assert.Equal(new[] { "Development", "Local" }, tree.FilteredSchemaNodes.Select(n => n.Name));
        var group = tree.FilteredSchemaNodes[0];
        Assert.Equal(SchemaNodeType.ConnectionGroup, group.Type);
        Assert.Equal(new[] { alpha, zulu }, group.Children);
        Assert.Same(local, tree.FilteredSchemaNodes[1]);
        tree.SchemaNodes.Remove(local);
        Assert.Single(tree.FilteredSchemaNodes);
    }

    [AvaloniaFact]
    public void GroupSearchShowsItsConnectionsWithoutCountingTheGroupAsATableMatch()
    {
        var connections = new Dictionary<SchemaNode, ConnectionInfo>();
        var tree = new SchemaTreeViewModel(connections, new(), new());
        var root = AddConnection(tree, connections, "Remote", "Production");
        root.Children.Add(new SchemaNode { Name = "orders", Type = SchemaNodeType.Table });
        AddConnection(tree, connections, "Local", "Development");

        tree.SchemaSearchText = "PRODUCTION";

        var group = Assert.Single(tree.FilteredSchemaNodes);
        Assert.Equal("Production", group.Name);
        Assert.Same(root, Assert.Single(group.Children));
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);
    }

    [AvaloniaFact]
    public void GroupEditsRebuildTheTreeAndResolveFilteredConnectionCopies()
    {
        var connections = new Dictionary<SchemaNode, ConnectionInfo>();
        var tree = new SchemaTreeViewModel(connections, new(), new());
        var root = AddConnection(tree, connections, "Remote", "Old");
        root.Children.Add(new SchemaNode { Name = "orders", Type = SchemaNodeType.Table });
        tree.RenameActiveGroup("Old", "New");
        Assert.Equal("New", Assert.Single(tree.FilteredSchemaNodes).Name);

        tree.SchemaSearchText = "orders";
        var copy = Assert.Single(Assert.Single(tree.FilteredSchemaNodes).Children);
        tree.MoveConnectionToGroup(copy, "Moved");
        Assert.Equal("Moved", connections[root].Group);
        Assert.Equal("Moved", Assert.Single(tree.FilteredSchemaNodes).Name);
        tree.DeleteActiveGroup("Moved");
        Assert.Equal("", connections[root].Group);
        Assert.Same(root, Assert.Single(tree.FilteredSchemaNodes).RealNode);
    }

    private static SchemaNode AddConnection(SchemaTreeViewModel tree,
        Dictionary<SchemaNode, ConnectionInfo> connections, string name, string group)
    {
        var root = new SchemaNode { Name = name, Type = SchemaNodeType.Database };
        connections.Add(root, new ConnectionInfo { Name = name, Group = group });
        tree.SchemaNodes.Add(root);
        return root;
    }

    [AvaloniaFact]
    public void SearchMatchesRawNamesAndKeepsTheRealDatabaseForActions()
    {
        var tree = new SchemaTreeViewModel(new(), new(), new());
        var root = new SchemaNode { Name = "Local", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "Customer records", RawName = "customers", Type = SchemaNodeType.Table };
        root.Children.Add(table);
        root.Children.Add(new SchemaNode { Name = "orders", Type = SchemaNodeType.Table });
        tree.SchemaNodes.Add(root);

        tree.SchemaSearchText = "  CUSTOMERS  ";

        var filtered = Assert.Single(tree.FilteredSchemaNodes);
        Assert.Same(root, filtered.RealNode);
        Assert.Same(table, Assert.Single(filtered.Children));
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);
        Assert.Equal(2, root.Children.Count);
        tree.SchemaSearchText = "missing";
        Assert.Empty(tree.FilteredSchemaNodes);
        Assert.Equal("No matches", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "";
        Assert.Same(root, Assert.Single(tree.FilteredSchemaNodes));
        Assert.Equal("", tree.SchemaFilterMatchCount);
    }
}
