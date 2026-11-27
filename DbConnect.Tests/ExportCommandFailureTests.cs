using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public sealed class ExportCommandFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-fault-{Guid.NewGuid():N}");
    public ExportCommandFailureTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "write")]
    [InlineData(false, "flush")]
    [InlineData(true, "flush")]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    [InlineData(false, "publish")]
    [InlineData(true, "publish")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(true, "all-write")]
    [InlineData(true, "all-flush")]
    [InlineData(true, "all-close")]
    [InlineData(true, "all-publish")]
    [InlineData(true, "all-cleanup")]
    [InlineData(true, "all-cancel")]
    [InlineData(true, "all-write", ExportFormat.Json)]
    [InlineData(true, "all-flush", ExportFormat.JsonLines)]
    [InlineData(true, "all-close", ExportFormat.Markdown)]
    [InlineData(true, "all-publish", ExportFormat.Json)]
    [InlineData(true, "all-cleanup", ExportFormat.JsonLines)]
    [InlineData(true, "all-cancel", ExportFormat.Markdown)]
    [InlineData(false, "write", ExportFormat.Markdown)]
    [InlineData(true, "write", ExportFormat.Markdown)]
    [InlineData(false, "flush", ExportFormat.Markdown)]
    [InlineData(true, "close", ExportFormat.Markdown)]
    [InlineData(false, "publish", ExportFormat.Markdown)]
    [InlineData(true, "cancel", ExportFormat.Markdown)]
    public async Task BothCommandsPreserveDestinationOnWriteFinalizePublishFailureAndCancel(bool table, string stage, ExportFormat format = ExportFormat.Csv)
    {
        var allRows = stage.StartsWith("all-", StringComparison.Ordinal);
        if (allRows) stage = stage[4..];
        var path = Path.Combine(_directory, "result.csv");
        await File.WriteAllTextAsync(path, "original bytes");
        ExportOperationViewModel state;
        Func<Task> run;
        if (table)
        {
            var page = CreatePage([1]);
            if (allRows) page.Result.ObjectRows[0][1] = new string('x', 100_000);
            var tab = new TableDataTabViewModel("Customers", CreateSession(new TablePagingProvider(page)), CreateTableNode(true));
            await tab.RefreshInternalAsync(true);
            tab.ExportPathPicker = new Picker(path);
            tab.ExportOptions.Format = format;
            if (allRows) tab.ExportScope = ExportScope.AllMatchingTableRows;
            state = tab.ExportState;
            run = () => tab.ExportCommand.ExecuteAsync(null);
        }
        else
        {
            var tab = new QueryTabViewModel { ExportPathPicker = new Picker(path) };
            tab.ExportOptions.Format = format;
            tab.SetExecutionResults([CreatePage([1]).Result]);
            state = tab.ExportState;
            run = () => tab.ExportCommand.ExecuteAsync(null);
        }
        var files = new FaultFiles(stage);
        state.FileOperations = files;
        var task = run();
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(state.IsRunning);
        if (stage == "cancel") state.CancelCommand.Execute(null);
        files.Release.SetResult();
        await task;
        Assert.Equal(stage == "cancel" ? ExportStatus.Canceled : ExportStatus.Failed, state.Outcome!.Status);
        Assert.Null(state.Outcome.FinalPath);
        Assert.Equal("original bytes", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("secret", state.Message);
        if (stage == "cleanup")
        {
            Assert.True(File.Exists(state.Outcome.RetainedTemporaryPath));
            Assert.Contains(state.Outcome.RetainedTemporaryPath!, state.Message);
        }
        else
        {
            Assert.Null(state.Outcome.RetainedTemporaryPath);
            Assert.Equal([path], Directory.GetFiles(_directory));
        }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }

    private sealed class FaultFiles(string stage) : IExportFileOperations
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream CreateTemporary(string path) => new FaultStream(path, this, stage);
        public void Publish(string temporaryPath, string destinationPath) => throw new IOException("secret publish error");
        public void DeleteTemporary(string path)
        {
            if (stage == "cleanup") throw new IOException("secret cleanup error");
            File.Delete(path);
        }
    }

    private sealed class FaultStream(string path, FaultFiles owner, string stage)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true)
    {
        private async Task GateAsync(CancellationToken token)
        {
            owner.Entered.TrySetResult();
            await owner.Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await GateAsync(token);
            if (stage is "write" or "cleanup") throw new IOException("secret write error");
            await base.WriteAsync(buffer, token);
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            if (stage == "flush") throw new IOException("secret flush error");
            await base.FlushAsync(token);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (stage == "close") throw new IOException("secret close error");
        }
    }
}
