using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class TableExportResponsivenessTests
{
    [AvaloniaFact]
    public async Task SynchronousPageFetchLeavesUiCancelAndUnrelatedTabUsable()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var fetching = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Mock<IDbSession>();
        inner.Setup(s => s.LoadTablePageAsync(It.IsAny<string>(), It.IsAny<TableQuerySpec>(), It.IsAny<QueryExecutionOptions>()))
            .Returns((string _, TableQuerySpec query, QueryExecutionOptions options) =>
            {
                if (query.PageIndex == 0) return Task.FromResult(new TablePageResult
                {
                    HasNextPage = true,
                    Result = new QueryResult
                    {
                        ColumnInfos = [new ColumnInfo { Name = "Id" }],
                        ObjectRows = [[new string('x', 100000)]]
                    }
                });
                fetching.TrySetResult(Environment.CurrentManagedThreadId);
                if (!options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("UI did not cancel the synchronous page fetch.");
                options.CancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new TablePageResult());
            });
        await using var reserved = new ExportDbSession(inner.Object);
        var provider = new TableDataTestFixtures.TablePagingProvider(TableDataTestFixtures.CreatePage([]));
        var tab = new TableDataTabViewModel("Customers", TableDataTestFixtures.CreateSession(provider), TableDataTestFixtures.CreateTableNode(true))
        { ShowExportPanel = true };
        var other = new TableDataTabViewModel("Customers", TableDataTestFixtures.CreateSession(provider), TableDataTestFixtures.CreateTableNode(true));
        var view = new TableDataTabView { DataContext = tab };
        var path = Path.Combine(Path.GetTempPath(), $"rain-responsive-{Guid.NewGuid():N}.csv");
        try
        {
            var source = new TableExportSource(_ => Task.FromResult<IDbSession>(reserved), "Customers", new([], 0, 10), [new("Id", 0)]);
            var updatesOnUi = true;
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.ExportState.PropertyChanged += (_, _) =>
            {
                updatesOnUi &= Dispatcher.UIThread.CheckAccess();
                if (tab.ExportState.RowsWritten > 0) written.TrySetResult();
            };
            var operation = tab.ExportState.RunAsync(new Picker(path), source, new(), ExportScope.AllMatchingTableRows, "test");
            Assert.NotEqual(uiThread, await fetching.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await written.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.True(tab.ExportState.IsRunning);
            var cancel = view.FindControl<Button>("CancelExportButton")!;
            Assert.True(cancel.IsVisible);
            Assert.True(cancel.Command!.CanExecute(null));
            await other.RefreshInternalAsync(true);
            Assert.False(other.IsLoading);
            Assert.True(provider.PageLoads > 0);
            cancel.Command.Execute(null);
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
            Assert.True(updatesOnUi);
            Dispatcher.UIThread.RunJobs();
            Assert.False(cancel.IsVisible);
            Assert.False(File.Exists(path));
        }
        finally
        {
            tab.ExportState.CancelCommand.Execute(null);
            await other.ReleaseDbSessionAsync();
            File.Delete(path);
        }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
