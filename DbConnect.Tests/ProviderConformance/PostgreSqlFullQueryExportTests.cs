using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.ProviderConformance.PostgreSqlExportEnvironment;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class PostgreSqlFullQueryExportTests
{
    private static QueryTabViewModel CreateTab(string name = "postgres-export", string tier = "Development")
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = name,
            Provider = "PostgreSQL",
            EnvironmentTier = tier,
            ConnectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL")!
        });
        return new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
    }

    [PostgreSqlExportTheory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Excel)]
    [InlineData(ExportFormat.Markdown)]
    public async Task EveryFormatUsesRecordedQueryAndPreservesDestinationOnLateReaderError(ExportFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-pg-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var table = $"rain_export_items_{Guid.NewGuid():N}";
        var tab = CreateTab();
        await using var other = await OpenAsync();
        try
        {
            var session = await tab.GetOrOpenDbSessionAsync();
            Assert.False((await session.ExecuteQueryAsync($"CREATE TABLE {table} AS SELECT i AS id, i::text AS value FROM generate_series(0,9999) AS i")).HasError);
            var parameters = new object?[] { "0" };
            var selected = await session.ExecuteQueryAsync($"SELECT CAST(value AS BIGINT) AS value FROM {table} WHERE id >= @p0",
                new QueryExecutionOptions { MaxResultRows = 1, Parameters = parameters });
            Assert.False(selected.HasError, selected.Error?.Summary);
            Assert.True(selected.IsTruncated);
            tab.SetExecutionResults([selected]);
            parameters[0] = "200000";
            selected.ExecutedSql = "DROP TABLE unrelated";
            tab.QueryText = "DROP TABLE unrelated";
            tab.ResultFilterText = "0";
            tab.ResultSortDescending = true;
            tab.ExportScope = ExportScope.FullQuery;
            tab.ExportOptions.Format = format;
            var path = Path.Combine(directory, "export");
            tab.ExportPathPicker = new Picker(path);
            tab.ConfirmFullQueryExport = disclosure =>
            {
                Assert.Contains("postgres-export", disclosure);
                Assert.Contains("original session and transaction", disclosure);
                Assert.Contains("client-only filtering/sorting", disclosure);
                Assert.Contains("Unsaved result edits", disclosure);
                return Task.FromResult(true);
            };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            Assert.Equal(10000, tab.ExportState.Outcome.RowsWritten);
            Assert.Equal(ExportScope.FullQuery, tab.ExportState.Outcome.Scope);
            var previous = await File.ReadAllBytesAsync(path);
            Assert.False((await other.ExecuteQueryAsync($"INSERT INTO {table} SELECT i, CASE WHEN i=99999 THEN 'private-invalid-value' ELSE i::text END FROM generate_series(10000,99999) AS i")).HasError);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome.Status);
            Assert.True(tab.ExportState.Outcome.RowsWritten > 0);
            Assert.Null(tab.ExportState.Outcome.FinalPath);
            Assert.DoesNotContain("private-invalid-value", tab.ExportState.Message);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
            Assert.False(tab.IsExecuting);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            await other.ExecuteQueryAsync($"DROP TABLE IF EXISTS {table}");
            Directory.Delete(directory, true);
        }
    }

    [PostgreSqlExportTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationAndCancelPreserveProductionDestinationAndUserTransaction(bool confirm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-pg-cancel-{Guid.NewGuid():N}.csv");
        var tab = CreateTab("production-postgres", "Production");
        try
        {
            await File.WriteAllTextAsync(path, "previous");
            var session = await tab.GetOrOpenDbSessionAsync();
            await session.BeginTransactionAsync();
            tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT 1 AS value UNION ALL SELECT 2", new QueryExecutionOptions { MaxResultRows = 1 })]);
            tab.ExportScope = ExportScope.FullQuery;
            var picker = new Picker(path);
            tab.ExportPathPicker = picker;
            tab.ConfirmFullQueryExport = disclosure =>
            {
                Assert.Contains("Production", disclosure);
                Assert.Contains("production-postgres", disclosure);
                return Task.FromResult(confirm);
            };
            tab.ExportState.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ExportOperationViewModel.RowsWritten) && tab.ExportState.RowsWritten == 1)
                    tab.ExportState.CancelCommand.Execute(null);
            };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Canceled, tab.ExportState.Outcome!.Status);
            Assert.Equal(confirm ? 1 : 0, picker.Calls);
            Assert.Equal(confirm ? 1 : 0, tab.ExportState.Outcome.RowsWritten);
            Assert.Equal("previous", await File.ReadAllTextAsync(path));
            Assert.Null(tab.ExportState.Outcome.FinalPath);
            Assert.False(tab.IsExecuting);
            Assert.True(session.IsInTransaction);
            await session.RollbackAsync();
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally { await tab.ReleaseDbSessionAsync(); File.Delete(path); }
    }

    [PostgreSqlExportTheory]
    [InlineData("write")]
    [InlineData("close")]
    [InlineData("publish")]
    public async Task FileFailuresDisposeReaderAndPreserveDestinationAndTransaction(string stage)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-pg-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "export.csv");
        var tab = CreateTab();
        try
        {
            await File.WriteAllTextAsync(path, "previous");
            var session = await tab.GetOrOpenDbSessionAsync();
            await session.BeginTransactionAsync();
            tab.SetExecutionResults([await session.ExecuteQueryAsync("SELECT 1 AS value UNION ALL SELECT 2", new QueryExecutionOptions { MaxResultRows = 1 })]);
            tab.ExportScope = ExportScope.FullQuery;
            tab.ConfirmFullQueryExport = _ => Task.FromResult(true);
            tab.ExportPathPicker = new Picker(path);
            tab.ExportState.FileOperations = new FaultFiles(stage);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome!.Status);
            Assert.Null(tab.ExportState.Outcome.FinalPath);
            Assert.Equal("previous", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
            Assert.True(session.IsInTransaction);
            Assert.False((await session.ExecuteQueryAsync("SELECT 3")).HasError);
        }
        finally { await tab.ReleaseDbSessionAsync(); Directory.Delete(directory, true); }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public int Calls { get; private set; }
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) { Calls++; return Task.FromResult<string?>(path); }
    }

    private sealed class FaultFiles(string stage) : IExportFileOperations
    {
        public Stream CreateTemporary(string path) => new FaultStream(path, stage);
        public void Publish(string temporaryPath, string destinationPath) => throw new IOException("Simulated publication failure.");
        public void DeleteTemporary(string path) => File.Delete(path);
    }

    private sealed class FaultStream(string path, string stage) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            stage == "write" ? ValueTask.FromException(new IOException("Simulated write failure.")) : base.WriteAsync(buffer, cancellationToken);
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (stage == "close") throw new IOException("Simulated finalization failure.");
        }
    }
}
