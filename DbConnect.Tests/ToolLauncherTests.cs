using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class ToolLauncherTests : MainWindowTestBase
{
    [Fact]
    public void DataMasking_ReusesAndSelectsTheExistingTab()
    {
        var workspace = CreateMainWindowViewModel();
        workspace.Tools.OpenMaskingRulesCommand.Execute(null);
        var tab = Assert.IsType<MaskingRulesViewModel>(workspace.SelectedTab);
        workspace.SelectedTab = null;

        workspace.Tools.OpenMaskingRulesCommand.Execute(null);

        Assert.Same(tab, workspace.SelectedTab);
        Assert.Single(workspace.Tabs.OfType<MaskingRulesViewModel>());
    }

    [Fact]
    public void NodeDiagram_UsesThatNodesSessionAndOpensItsDiffInTheWorkspace()
    {
        var workspace = CreateMainWindowViewModel();
        var first = new DatabaseSession(new ConnectionInfo(), new SqliteProvider());
        var selected = new DatabaseSession(new ConnectionInfo(), new SqliteProvider());
        var node = new SchemaNode { Type = SchemaNodeType.Database };
        workspace.ActiveSessions[node] = selected;
        workspace.ActiveSessionOptions.Add(first);
        workspace.ActiveSessionOptions.Add(selected);

        workspace.Tools.OpenErDiagramForNode(node);

        var diagram = Assert.IsType<ErDiagramViewModel>(workspace.SelectedTab);
        Assert.Same(selected, diagram.Session);
        var diff = new SchemaDiffViewModel(workspace.ActiveSessionOptions);
        diagram.RequestOpenDiffTab!(diff);
        Assert.Same(diff, workspace.SelectedTab);
        Assert.Contains(diff, workspace.Tabs);
        diff.RequestOpenInTab!("select 42;");
        Assert.Equal("select 42;", Assert.IsType<QueryTabViewModel>(workspace.SelectedTab).QueryText);
    }
}
