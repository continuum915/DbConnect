using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class FullQueryExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-full-query-{Guid.NewGuid():N}");
    public FullQueryExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task FullQueryWithoutRecoverableContextFailsWithoutOpeningPicker()
    {
        var picker = new Picker(Path.Combine(_directory, "export.csv"));
        var tab = new QueryTabViewModel { ExportPathPicker = picker, ExportScope = ExportScope.FullQuery };
        tab.SetExecutionResults([new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "value", Ordinal = 0 }],
            ObjectRows = [[1L]], ExecutedSql = "SELECT 1"
        }]);

        await tab.ExportCommand.ExecuteAsync(null);

        Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome!.Status);
        Assert.Equal(ExportScope.FullQuery, tab.ExportState.Outcome.Scope);
        Assert.Equal(0, picker.Calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class Picker(string? path) : IExportPathPicker
    {
        public int Calls { get; private set; }
        public Task<string?> PickSavePathAsync(string name, ExportFileType type)
        {
            Calls++;
            return Task.FromResult(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RerunRequiresExplicitConfirmationBeforePickerAndDisclosesProductionTarget(bool decline)
    {
        var tab = await CreateTabAsync();
        try
        {
            tab.SelectedSession!.Connection.EnvironmentTier = "Production";
            var picker = new Picker(Path.Combine(_directory, "declined.csv"));
            tab.ExportPathPicker = picker;
            if (decline) tab.ConfirmFullQueryExport = disclosure =>
            {
                Assert.Contains("Production", disclosure);
                Assert.Contains("full-export", disclosure);
                Assert.Contains("Data may have changed", disclosure);
                Assert.Contains("costly", disclosure);
                Assert.Contains("Unsaved result edits", disclosure);
                Assert.Contains("client-only filtering/sorting", disclosure);
                return Task.FromResult(false);
            };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
            Assert.Equal(0, picker.Calls);
            Assert.False(tab.IsExecuting);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    [Fact]
    public async Task ConfirmationAndPickerAwaitsCannotChangeCapturedScopeResultOrOptions()
    {
        var tab = await CreateTabAsync();
        try
        {
            var original = tab.CurrentResult;
            var confirm = new TaskCompletionSource<bool>();
            tab.ConfirmFullQueryExport = _ => confirm.Task;
            var picker = new PendingPicker();
            tab.ExportPathPicker = picker;
            var exporting = tab.ExportCommand.ExecuteAsync(null);
            Assert.True(tab.IsExecuting);
            Assert.False(tab.CanChangeConnection);
            tab.ExportScope = ExportScope.DisplayedQueryResults;
            tab.ExportOptions.Format = ExportFormat.Json;
            tab.CurrentResult = new QueryResult();
            confirm.SetResult(true);
            await picker.Started.Task;
            tab.QueryText = "DELETE FROM items";
            picker.Result.SetResult(Path.Combine(_directory, "captured.csv"));
            await exporting;
            Assert.Equal(ExportScope.FullQuery, tab.ExportState.Outcome!.Scope);
            Assert.Equal(ExportFormat.Csv, tab.ExportState.Outcome.Format);
            Assert.Equal("value\r\n1\r\n2\r\n3\r\n", await File.ReadAllTextAsync(tab.ExportState.Outcome.FinalPath!));
            Assert.Contains(tab.SelectedSession!.Connection.Id.ToString(), tab.ExportState.Outcome.SourceIdentity);
            Assert.NotNull(original);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    [Fact]
    public async Task PickerCancellationDoesNotReadDatabaseEvenWhenSessionBecomesUnusable()
    {
        var tab = await CreateTabAsync();
        tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
        var picker = new PendingPicker();
        tab.ExportPathPicker = picker;
        var exporting = tab.ExportCommand.ExecuteAsync(null);
        await picker.Started.Task;
        await tab.ReleaseDbSessionAsync();
        picker.Result.SetResult(null);
        await exporting;
        Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
        Assert.Equal(0, tab.ExportState.Outcome.RowsWritten);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Excel)]
    [InlineData(ExportFormat.Markdown)]
    public async Task EveryFormatStreamsBeyondCapAndReaderFailurePreservesDestination(ExportFormat format)
    {
        var tab = await CreateTabAsync();
        try
        {
            var path = Path.Combine(_directory, "export");
            tab.ExportOptions.Format = format;
            tab.ExportPathPicker = new Picker(path);
            tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            Assert.Equal(3, tab.ExportState.Outcome.RowsWritten);
            var originalBytes = await File.ReadAllBytesAsync(path);
            var session = await tab.GetOrOpenDbSessionAsync();
            var result = await session.ExecuteQueryAsync("SELECT CASE WHEN value=3 THEN abs(-9223372036854775808) ELSE value END AS value FROM items",
                new QueryExecutionOptions { MaxResultRows = 1 });
            Assert.False(result.HasError);
            tab.SetExecutionResults([result]);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome.Status);
            Assert.Equal(2, tab.ExportState.Outcome.RowsWritten);
            Assert.Null(tab.ExportState.Outcome.FinalPath);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(_directory));
            Assert.False((await session.ExecuteQueryAsync("SELECT value FROM items")).HasError);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    private static async Task<QueryTabViewModel> CreateTabAsync()
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "full-export",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        var session = await tab.GetOrOpenDbSessionAsync();
        await session.ExecuteQueryAsync("CREATE TEMP TABLE items(value INTEGER); INSERT INTO items VALUES(1),(2),(3);");
        tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT value FROM items", new QueryExecutionOptions { MaxResultRows = 1 })]);
        tab.ExportScope = ExportScope.FullQuery;
        return tab;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringStreamingDoesNotPublishAndLeavesTransactionUsable(bool queryCancel)
    {
        var tab = await CreateTabAsync();
        try
        {
            var session = await tab.GetOrOpenDbSessionAsync();
            await session.BeginTransactionAsync();
            tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT value FROM items", new QueryExecutionOptions { MaxResultRows = 1 })]);
            tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
            tab.ExportPathPicker = new Picker(Path.Combine(_directory, "cancel.csv"));
            tab.ExportState.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ExportOperationViewModel.RowsWritten) && tab.ExportState.RowsWritten == 1)
                {
                    if (queryCancel) tab.CancelExecutionCommand.Execute(null);
                    else tab.ExportState.CancelCommand.Execute(null);
                }
            };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
            Assert.Equal(1, tab.ExportState.Outcome.RowsWritten);
            Assert.Empty(Directory.GetFiles(_directory));
            Assert.True(session.IsInTransaction);
            Assert.False((await session.ExecuteQueryAsync("SELECT value FROM items")).HasError);
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }

    private sealed class PendingPicker : IExportPathPicker
    {
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource<string?> Result { get; } = new();
        public Task<string?> PickSavePathAsync(string name, ExportFileType type)
        {
            Started.SetResult();
            return Result.Task;
        }
    }

    [Fact]
    public async Task FullQueryRerunsSelectedResultWithOriginalParametersBeyondCapDespiteEditorChanges()
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "full-export",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        var session = await tab.GetOrOpenDbSessionAsync();
        try
        {
            await session.ExecuteQueryAsync("CREATE TEMP TABLE items(value INTEGER); INSERT INTO items VALUES(1),(2),(3),(4),(5);");
            var parameters = new object?[] { 2L };
            var selected = await session.ExecuteQueryAsync("SELECT value FROM items WHERE value >= @p0 ORDER BY value",
                new QueryExecutionOptions { MaxResultRows = 1, Parameters = parameters });
            var other = await session.ExecuteQueryAsync("SELECT 99 AS other");
            tab.SetExecutionResults([other, selected], selected);
            parameters[0] = 100L;
            tab.QueryText = "DROP TABLE items";
            tab.ResultFilterText = "2";
            tab.ExportScope = ExportScope.FullQuery;
            var path = Path.Combine(_directory, "full.csv");
            tab.ExportPathPicker = new Picker(path);
            tab.ConfirmFullQueryExport = disclosure => Task.FromResult(true);

            await tab.ExportCommand.ExecuteAsync(null);

            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            Assert.Equal(ExportScope.FullQuery, tab.ExportState.Outcome.Scope);
            Assert.Equal(4, tab.ExportState.Outcome.RowsWritten);
            Assert.Equal("value\r\n2\r\n3\r\n4\r\n5\r\n", await File.ReadAllTextAsync(path));
        }
        finally { await tab.ReleaseDbSessionAsync(); }
    }
}
