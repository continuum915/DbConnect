using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class ExportStreamingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-stream-{Guid.NewGuid():N}");
    public ExportStreamingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, ExportFormat.Json)]
    [InlineData(true, ExportFormat.Json)]
    [InlineData(false, ExportFormat.JsonLines)]
    [InlineData(true, ExportFormat.JsonLines)]
    [InlineData(false, ExportFormat.Markdown)]
    [InlineData(true, ExportFormat.Markdown)]
    [InlineData(false, ExportFormat.Excel)]
    [InlineData(true, ExportFormat.Excel)]
    public async Task InterruptedReadPreservesExistingDestinationAndCleansOwnedFile(bool cancel, ExportFormat format = ExportFormat.Csv)
    {
        var path = Path.Combine(_directory, "result.csv");
        await File.WriteAllTextAsync(path, "previous bytes");
        using var cancellation = new CancellationTokenSource();
        var source = new InterruptedSource(cancellation, cancel);
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(source, new() { Format = format }, ExportScope.DisplayedQueryResults,
            cancellationToken: cancellation.Token);
        Assert.Equal(cancel ? ExportStatus.Canceled : ExportStatus.Failed, outcome.Status);
        Assert.Equal(1, outcome.RowsWritten);
        Assert.Null(outcome.FinalPath);
        Assert.Equal("previous bytes", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
        Assert.DoesNotContain("secret", outcome.Diagnostic ?? "");
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task PublicationFailureDoesNotPublishOrRetainOwnedFile()
    {
        var outcome = await new ResultExporter(new Picker(_directory)).ExportAsync(
            new LoadedExportSource([new("value", 0)], [["row"]]), new(), ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Null(outcome.FinalPath);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.Excel)]
    public async Task WritesBeforeSourceExhaustionWithoutReadingAhead(ExportFormat format)
    {
        var source = new GatedSource(format == ExportFormat.Excel ? 22000 : 100000);
        var path = Path.Combine(_directory, "result.csv");
        var task = new ResultExporter(new Picker(path)).ExportAsync(source, new() { Format = format }, ExportScope.DisplayedQueryResults);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var temporary = Assert.Single(Directory.GetFiles(_directory));
            Assert.True(new FileInfo(temporary).Length > 0);
            Assert.False(File.Exists(path));
            Assert.Equal(2, source.Consumed);
        }
        finally { source.Release.SetResult(); await task; }
        Assert.Equal(ExportStatus.Completed, (await task).Status);
    }

    [Fact]
    public async Task EmptySourcePublishesHeadersAndReplacesExistingDestination()
    {
        var path = Path.Combine(_directory, "result.csv");
        await File.WriteAllTextAsync(path, "old");
        var result = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new("id", 0), new("id", 1)], []), new(), ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Completed, result.Status);
        Assert.Equal(0, result.RowsWritten);
        Assert.Equal("id,id\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PickerCancellationDoesNotConsumeSource()
    {
        var source = new GatedSource();
        var result = await new ResultExporter(new Picker(null)).ExportAsync(source, new(), ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Canceled, result.Status);
        Assert.Equal(0, source.Consumed);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class Picker(string? path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult(path);
    }

    private sealed class InterruptedSource(CancellationTokenSource cancellation, bool cancel) : IExportSource
    {
        public IReadOnlyList<ExportColumn> Columns { get; } = [new("value", 0)];
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return ["first"];
                await Task.Yield();
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new IOException("secret SQL / connection password");
            }
            finally { Disposed = true; }
        }
    }

    private sealed class GatedSource(int payloadBytes = 100000) : IExportSource
    {
        public IReadOnlyList<ExportColumn> Columns { get; } = [new("value", 0)];
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Consumed { get; private set; }
        public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < 2; i++)
            {
                Consumed++;
                yield return [Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(payloadBytes))];
            }
            Waiting.SetResult();
            await Release.Task.WaitAsync(token);
        }
    }
}
