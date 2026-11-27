using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class DuckDbFullQueryExportTests
{
    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    [InlineData(ExportFormat.Excel)]
    [InlineData(ExportFormat.Markdown)]
    public async Task EveryFormatUsesCapturedQueryAndPreservesDestinationOnLateReaderFailure(ExportFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-duck-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var info = new ConnectionInfo { Name = "duck-export", Provider = "DuckDB", ConnectionString = $"Data Source={Path.Combine(directory, "test.duckdb")}" };
        var connection = new DatabaseService().CreateSession(info);
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        try
        {
            var session = await tab.GetOrOpenDbSessionAsync();
            Assert.False((await session.ExecuteQueryAsync("SET threads=1")).HasError);
            Assert.False((await session.ExecuteQueryAsync("CREATE TABLE items AS SELECT i AS id, i::VARCHAR AS value FROM range(10000) t(i)")).HasError);
            var parameters = new object?[] { 0L };
            var selected = await session.ExecuteQueryAsync("SELECT CAST(value AS BIGINT) AS value FROM items WHERE id >= ?",
                new QueryExecutionOptions { MaxResultRows = 1, Parameters = parameters });
            Assert.False(selected.HasError, selected.Error?.Summary);
            Assert.True(selected.IsTruncated);
            tab.SetExecutionResults([selected]);
            parameters[0] = 20000L;
            tab.QueryText = "DROP TABLE items";
            tab.ResultFilterText = "0";
            tab.ExportScope = ExportScope.FullQuery;
            tab.ExportOptions.Format = format;
            var path = Path.Combine(directory, "export");
            tab.ExportPathPicker = new Picker(path);
            tab.ConfirmFullQueryExport = disclosure =>
            {
                Assert.Contains("duck-export", disclosure);
                Assert.Contains("original session and transaction", disclosure);
                return Task.FromResult(true);
            };
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
            Assert.Equal(10000, tab.ExportState.Outcome.RowsWritten);
            var previous = await File.ReadAllBytesAsync(path);
            await using var other = await new DuckDbProvider().OpenSessionAsync(info);
            Assert.False((await other.ExecuteQueryAsync("INSERT INTO items SELECT i, CASE WHEN i=249999 THEN 'invalid' ELSE i::VARCHAR END FROM range(10000,250000) t(i)")).HasError);
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Failed, tab.ExportState.Outcome.Status);
            Assert.True(tab.ExportState.Outcome.RowsWritten > 0);
            Assert.Null(tab.ExportState.Outcome.FinalPath);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(Directory.GetFiles(directory), p => Path.GetFileName(p).StartsWith(".dbconnect-export-", StringComparison.Ordinal));
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationAndCancellationPreserveProductionDestinationAndTransaction(bool confirm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-duck-cancel-{Guid.NewGuid():N}.csv");
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "production-duck",
            Provider = "DuckDB",
            EnvironmentTier = "Production",
            ConnectionString = "Data Source=:memory:"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
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
                Assert.Contains("production-duck", disclosure);
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
            Assert.False((await session.ExecuteQueryAsync("SELECT 1")).HasError);
        }
        finally { await tab.ReleaseDbSessionAsync(); File.Delete(path); }
    }
    private sealed class Picker(string path) : IExportPathPicker
    {
        public int Calls { get; private set; }
        public Task<string?> PickSavePathAsync(string name, ExportFileType type)
        {
            Calls++;
            return Task.FromResult<string?>(path);
        }
    }
}
