using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class R0SafetyTests
{
    [Fact]
    public async Task Automation_InvalidQuery_IsFailedRatherThanSuccessful()
    {
        var provider = new SqliteProvider();
        var session = CreateSession(provider, "Data Source=:memory:");
        var job = new AutomationJob
        {
            Type = JobType.RunQuery,
            SqlOrFilePath = "SELECT * FROM missing_table"
        };

        var record = await new AutomationService().ExecuteJobAsync(job, session, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Contains("missing_table", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automation_EmptyQuery_IsFailedRatherThanSuccessful()
    {
        var session = CreateSession(new SqliteProvider(), "Data Source=:memory:");
        var job = new AutomationJob { Type = JobType.RunQuery };

        var record = await new AutomationService().ExecuteJobAsync(job, session, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Contains("empty", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automation_CancelledQuery_IsCancelledRatherThanSuccessful()
    {
        var session = CreateSession(new SqliteProvider(), "Data Source=:memory:");
        var job = new AutomationJob { Type = JobType.RunQuery, SqlOrFilePath = "SELECT 1" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var record = await new AutomationService().ExecuteJobAsync(job, session, cancellation.Token);

        Assert.Equal(JobStatus.Cancelled, record.Status);
        Assert.Null(record.ErrorMessage);
    }

    [Fact]
    public async Task Automation_SqliteBackup_ReportsSuccessOnlyAfterCreatingFile()
    {
        var sourcePath = TempPath(".db");
        var backupPath = TempPath(".db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={sourcePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Items VALUES (1, 'Ada');";
                await command.ExecuteNonQueryAsync();
            }

            var session = CreateSession(new SqliteProvider(), $"Data Source={sourcePath}");
            var job = new AutomationJob { Type = JobType.Backup, SqlOrFilePath = backupPath };
            var record = await new AutomationService().ExecuteJobAsync(job, session, CancellationToken.None);

            Assert.Equal(JobStatus.Succeeded, record.Status);
            Assert.True(File.Exists(backupPath));
            Assert.True(new FileInfo(backupPath).Length > 0);
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(backupPath);
        }
    }

    [Theory]
    [InlineData(JobType.ExportResult)]
    [InlineData(JobType.ImportCsv)]
    public async Task Automation_UnimplementedFileJobs_AreFailed(JobType jobType)
    {
        var session = CreateSession(new SqliteProvider(), "Data Source=:memory:");
        var job = new AutomationJob { Type = jobType, SqlOrFilePath = "not-used" };

        var record = await new AutomationService().ExecuteJobAsync(job, session, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Contains("not implemented", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automation_UnsupportedProviderBackup_IsFailed()
    {
        // DuckDB has no backup strategy yet (mysql-duckdb-backup/03).
        var session = CreateSession(new DuckDbProvider(), "Data Source=:memory:");
        var job = new AutomationJob { Type = JobType.Backup, SqlOrFilePath = TempPath(".sql") };

        var record = await new AutomationService().ExecuteJobAsync(job, session, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Contains("not supported", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestructiveActionPreview_ExposesTargetEnvironmentAndSql()
    {
        var preview = new DestructiveActionPreview(
            "Drop",
            "TABLE",
            "Orders",
            "Production database",
            "Production",
            "DROP TABLE [Orders];");
        var viewModel = new ProductionConfirmViewModel(preview);

        Assert.Contains("Production database", viewModel.TargetText);
        Assert.Contains("Production", viewModel.TargetText);
        Assert.Contains("DROP TABLE [Orders];", viewModel.SqlPreview);
        Assert.Contains("Drop TABLE Orders", viewModel.ActionText);

        viewModel.TypedName = "Orders";
        viewModel.Confirm();
        Assert.True(viewModel.Confirmed);
    }

    private static DatabaseSession CreateSession(IDatabaseProvider provider, string connectionString) =>
        new(
            new ConnectionInfo
            {
                Name = "R0 safety test",
                Provider = provider.ProviderName,
                ConnectionString = connectionString
            },
            provider);

    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"rain-r0-safety-{Guid.NewGuid():N}{extension}");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
