using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class ExplorerSearchHeadlessTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task ShowsPendingFailureAndRetryThenSelectsAnUnexpandedDescendant()
    {
        var (source, session, provider, folder) = await UnexpandedSchemaSearchTests.CreateTree();
        using var catalog = session.Catalog;
        var workspace = CreateMainWindowViewModel();
        var root = source.SchemaNodes[0];
        workspace.ActiveSessions[root] = session;
        workspace.ActiveConnections[root] = session.Connection;
        workspace.SchemaNodes.Add(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<SchemaNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var structured = provider.As<IStructuredSchemaProvider>();
        structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var window = new MainWindow { DataContext = workspace };
        try
        {
            window.Show();
            workspace.SchemaSearchText = "orders";
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            var status = window.FindControl<TextBlock>("SchemaSearchStatus")!;
            var retry = window.FindControl<Button>("RetrySchemaSearch")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.Contains("Searching", status.Text);
            Assert.False(retry.IsEffectivelyVisible);
            release.SetException(new IOException("permission denied"));
            await workspace.SchemaTree.Search.Completion;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Incomplete", status.Text);
            Assert.True(retry.IsEffectivelyVisible);
            structured.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
                .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(retry.Command).ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("1 match", status.Text);
            Assert.False(retry.IsEffectivelyVisible);
            var relation = workspace.FilteredSchemaNodes[0].Children[0].Children[0].Children[0];
            var control = window.FindControl<TreeView>("SchemaTree")!;
            control.SelectedItem = relation;
            Assert.Same(relation, workspace.SelectedSchemaNode);
            Assert.Same(session, workspace.SchemaTree.FindSessionForNode(workspace.SelectedSchemaNode!));
            Assert.Equal("orders", workspace.SelectedSchemaNode!.Identity!.Name);
            Assert.False(folder.IsExpanded);
        }
        finally { window.Close(); }
    }
}
