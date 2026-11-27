using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class ThinCodeBehindTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData(SchemaNodeType.Table, true, false)]
    [InlineData(SchemaNodeType.Column, true, false)]
    [InlineData(SchemaNodeType.ConnectionGroup, false, true)]
    public void SchemaMenu_UpdatesAvailabilityAndSeparators(SchemaNodeType type, bool copyVisible, bool groupVisible)
    {
        var main = CreateMainWindowViewModel();
        var window = new MainWindow();
        try
        {
            window.DataContext = main;
            var node = new SchemaNode { Name = "Example", Type = type };
            main.SchemaNodes.Add(node);
            window.Show();
            var tree = window.FindControl<TreeView>("SchemaTree")!;
            tree.ItemsSource = new[] { node };
            window.UpdateLayout();
            tree.SelectedItem = node;
            Assert.Same(node, tree.SelectedItem);
            var point = tree.TranslatePoint(new Point(20, 15), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(tree.ContextMenu!.IsOpen);

            Assert.Equal(copyVisible, window.FindControl<MenuItem>("CtxCopyName")!.IsVisible);
            Assert.Equal(groupVisible, window.FindControl<MenuItem>("CtxRenameGroup")!.IsVisible);
            Assert.Equal(groupVisible, window.FindControl<Separator>("CtxSep10")!.IsVisible);
            Assert.Equal(type == SchemaNodeType.Table, window.FindControl<Separator>("CtxSep2")!.IsVisible);
            if (type == SchemaNodeType.Table)
            {
                var design = window.FindControl<MenuItem>("CtxDesignTable")!;
                Assert.True(design.IsVisible);
                Assert.False(design.IsEnabled);
                Assert.Equal("Connect before designing tables.", ToolTip.GetTip(design));
                foreach (var name in new[] { "CtxEmptyTable", "CtxTruncateTable", "CtxRenameObject", "CtxDuplicateTable", "CtxCopyDdl", "CtxExportData", "CtxDumpSql" })
                {
                    var action = window.FindControl<MenuItem>(name)!;
                    Assert.True(action.IsVisible, name);
                    Assert.False(action.IsEnabled, name);
                }
                Assert.Equal(["Structure and Data…", "Structure Only…"],
                    window.FindControl<MenuItem>("CtxDumpSql")!.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
                var scriptAs = window.FindControl<MenuItem>("CtxScriptAs")!;
                Assert.True(scriptAs.IsVisible);
                Assert.Equal(["CREATE", "DROP", "SELECT", "INSERT", "UPDATE", "DELETE"],
                    scriptAs.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
                Assert.Equal(["New Query Tab", "Clipboard", "File…"],
                    ((MenuItem)scriptAs.Items[0]!).Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
            }
            tree.ContextMenu.Close();
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CommentToggle_CanBeUndoneInOneStep()
    {
        var editor = new TextEditor { Text = "select 1;\nselect 2;" };
        editor.Select(0, editor.Text.Length);

        SqlEditorCommentBehavior.Toggle(editor);

        Assert.Equal("--select 1;\n--select 2;", editor.Text);
        editor.Undo();
        Assert.Equal("select 1;\nselect 2;", editor.Text);
    }

    [AvaloniaFact]
    public void CommentToggle_RemovesCommentsAfterIndentation()
    {
        var editor = new TextEditor { Text = "  --select 1;\n--select 2;" };
        editor.Select(0, editor.Text.Length);

        SqlEditorCommentBehavior.Toggle(editor);

        Assert.Equal("  select 1;\nselect 2;", editor.Text);
    }

    [Fact]
    public void MaskingRulesCommand_SelectsTheExistingTab()
    {
        var main = CreateMainWindowViewModel();
        main.Tools.OpenMaskingRulesCommand.Execute(null);
        var tab = Assert.IsType<MaskingRulesViewModel>(main.SelectedTab);
        main.SelectedTab = null;

        main.Tools.OpenMaskingRulesCommand.Execute(null);

        Assert.Same(tab, main.SelectedTab);
        Assert.Single(main.Tabs.OfType<MaskingRulesViewModel>());
    }

    [Fact]
    public void NodeDiagram_OpensAndConnectsDiffAndScriptTabs()
    {
        var main = CreateMainWindowViewModel();
        main.Tools.OpenErDiagramForNode(new SchemaNode { Type = SchemaNodeType.Database });
        var diagram = Assert.IsType<ErDiagramViewModel>(main.SelectedTab);
        Assert.Contains(diagram, main.Tabs);
        var diff = new SchemaDiffViewModel([]);

        diagram.RequestOpenDiffTab!(diff);

        Assert.Same(diff, main.SelectedTab);
        Assert.Contains(diff, main.Tabs);
        diff.RequestOpenInTab!("select 42;");
        Assert.Equal("select 42;", Assert.IsType<QueryTabViewModel>(main.SelectedTab).QueryText);
    }

    [Theory]
    [InlineData("rename", "New", "New")]
    [InlineData("delete", "", "")]
    [InlineData("move", "New", "Old")]
    public async Task GroupChanges_UpdateTheExplorerAndSavedConnections(
        string action, string activeGroup, string offlineGroup)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-groups-{Guid.NewGuid():N}");
        try
        {
            var settings = new ConnectionSettingsService(new MemoryStore(), directory);
            var root = new SchemaNode { Name = "Active", Type = SchemaNodeType.Database };
            var active = new ConnectionInfo { Name = "Active", Group = "Old" };
            var offline = new ConnectionInfo { Name = "Offline", Group = "Old" };
            var other = new ConnectionInfo { Name = "Other", Group = "Other" };
            await settings.SaveConnectionsAsync([active, offline, other]);
            var connections = new Dictionary<SchemaNode, ConnectionInfo> { [root] = active };
            var tree = new SchemaTreeViewModel(connections, new(), new());
            tree.SchemaNodes.Add(root);
            var groups = new ConnectionGroupActions(tree, connections, settings);

            if (action == "rename") await groups.RenameAsync("Old", "New");
            else if (action == "delete") await groups.DeleteAsync("Old");
            else await groups.MoveAsync(new SchemaNode { RealNode = root }, "New");

            Assert.Equal(activeGroup, active.Group);
            Assert.Equal(activeGroup == "" ? "Active" : activeGroup, Assert.Single(tree.FilteredSchemaNodes).Name);
            var saved = await settings.LoadConnectionsAsync();
            Assert.Equal(activeGroup, saved.Single(c => c.Name == "Active").Group);
            Assert.Equal(offlineGroup, saved.Single(c => c.Name == "Offline").Group);
            Assert.Equal("Other", saved.Single(c => c.Name == "Other").Group);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemoryStore : ICredentialStore
    {
        public Task SetAsync(string key, string secret) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
