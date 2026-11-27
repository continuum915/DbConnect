using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ExplorerSearchTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData(SchemaNodeType.Table)]
    [InlineData(SchemaNodeType.View)]
    [InlineData(SchemaNodeType.MaterializedView)]
    [InlineData(SchemaNodeType.VirtualTable)]
    public void SearchMatchesRawRelationNamesInsteadOfDecoratedLabels(SchemaNodeType kind)
    {
        var relation = new SchemaNode { Name = "friendly label", RawName = "order.items", Type = kind, ChildCount = 123 };
        var folder = new SchemaNode { Name = "Objects", Type = SchemaNodeType.TableFolder, IsLoaded = true, Children = [relation] };
        var schema = new SchemaNode { Name = "sales.eu", Type = SchemaNodeType.Schema, Children = [folder] };
        var root = new SchemaNode { Name = "Local", Type = SchemaNodeType.Database, Children = [schema] };
        var tree = new SchemaTreeViewModel(new(), new(), new());
        tree.SchemaNodes.Add(root);

        tree.SchemaSearchText = " ORDER.ITEMS ";
        var displayed = Assert.Single(tree.FilteredSchemaNodes);
        Assert.Same(root, displayed.RealNode);
        Assert.Same(schema, Assert.Single(displayed.Children).RealNode);
        var displayedFolder = Assert.Single(displayed.Children[0].Children);
        Assert.Same(folder, displayedFolder.RealNode);
        Assert.Same(relation, Assert.Single(displayedFolder.Children));
        Assert.Equal("1 match", tree.SchemaFilterMatchCount);

        tree.SchemaSearchText = "friendly";
        Assert.Empty(tree.FilteredSchemaNodes);
        Assert.Equal("No matches", tree.SchemaFilterMatchCount);
        tree.SchemaSearchText = "123";
        Assert.Empty(tree.FilteredSchemaNodes);
        tree.SchemaSearchText = "";
        Assert.Same(root, Assert.Single(tree.FilteredSchemaNodes));
        Assert.Same(folder, root.Children[0].Children[0]);
        Assert.True(folder.IsLoaded);
        Assert.Equal("", tree.SchemaFilterMatchCount);
    }

    [AvaloniaFact]
    public void GroupSearchCountsDisplayedRelationsAndFilteredActionsKeepDuplicateAddresses()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tabs.Clear();
        var originals = new List<SchemaNode>();
        foreach (var schemaName in new[] { "sales.eu", "archive" })
        {
            var table = new SchemaNode { Name = "orders", RawName = "orders", Type = SchemaNodeType.Table, SchemaPath = schemaName };
            var schema = new SchemaNode
            {
                Name = schemaName,
                Type = SchemaNodeType.Schema,
                IsExpanded = true,
                Children = [new SchemaNode { Type = SchemaNodeType.TableFolder, Children = [table] }]
            };
            var root = new SchemaNode { Name = "duplicate", Type = SchemaNodeType.Database, Children = [schema] };
            var session = new DatabaseSession(new ConnectionInfo { Group = "Production" }, new PostgreSqlProvider());
            session.Objects.Bind(root, null);
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            originals.Add(root);
        }

        var originalGroup = Assert.Single(workspace.FilteredSchemaNodes);
        originalGroup.IsExpanded = true;
        workspace.SchemaSearchText = "production";
        Assert.Equal("2 matches", workspace.SchemaFilterMatchCount);
        Assert.Equal(2, Assert.Single(workspace.FilteredSchemaNodes).Children.Count);
        workspace.SchemaSearchText = "orders";
        var group = Assert.Single(workspace.FilteredSchemaNodes);
        foreach (var root in group.Children)
        {
            var folder = Assert.Single(Assert.Single(root.Children).Children);
            var table = Assert.Single(folder.Children);
            var session = workspace.SchemaTree.FindSessionForNode(folder);
            Assert.NotNull(session);
            Assert.Same(session.Connection, workspace.SchemaTree.FindConnectionForNode(table));
            Assert.Equal(table.Identity, workspace.SchemaTree.GetIdentity(table, session));
            workspace.OpenTableDataTab(table);
            var tab = Assert.IsType<TableDataTabViewModel>(workspace.SelectedTab);
            Assert.Equal(table.Identity, tab.ObjectSelection.Identity);
        }
        Assert.Equal(2, workspace.Tabs.Count);
        workspace.SchemaSearchText = "";
        Assert.Same(originalGroup, Assert.Single(workspace.FilteredSchemaNodes));
        Assert.True(originalGroup.IsExpanded);
        Assert.All(Assert.Single(workspace.FilteredSchemaNodes).Children, root =>
        {
            Assert.Contains(root, originals);
            Assert.True(root.Children[0].IsExpanded);
        });
    }
}
