using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Tier 1 §1.8: automation runs only on demand and says so; nothing pretends to schedule.
/// </summary>
public sealed class AutomationOnDemandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-automation-{Guid.NewGuid():N}");
    private readonly AutomationService _service;

    public AutomationOnDemandTests() => _service = new AutomationService(dataDirectory: _directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task RunNow_UsesOpenSessionWithJobConnectionName_AndRecordsHistory()
    {
        var sessions = new ObservableCollection<DatabaseSession> { CreateSession("Local") };
        var vm = new AutomationViewModel(sessions, _service);
        var job = await SaveJobAsync("Local", "SELECT 1");
        try
        {
            await vm.LoadJobs();
            vm.SelectedJob = vm.Jobs.Single(j => j.Id == job.Id);

            await vm.RunNowCommand.ExecuteAsync(null);

            var record = Assert.Single(vm.RunHistory);
            Assert.Equal(JobStatus.Succeeded, record.Status);
            Assert.Contains("succeeded", vm.StatusMessage);
            Assert.Single(await _service.LoadRunHistoryAsync(job.Id));
        }
        finally
        {
            await _service.DeleteJobAsync(job.Id);
        }
    }

    [Fact]
    public async Task RunNow_WithoutMatchingOpenSession_ExplainsAndRecordsNothing()
    {
        var sessions = new ObservableCollection<DatabaseSession> { CreateSession("Other") };
        var vm = new AutomationViewModel(sessions, _service);
        var job = await SaveJobAsync("Local", "SELECT 1");
        try
        {
            await vm.LoadJobs();
            vm.SelectedJob = vm.Jobs.Single(j => j.Id == job.Id);

            await vm.RunNowCommand.ExecuteAsync(null);

            Assert.Empty(vm.RunHistory);
            Assert.Contains("Open the connection \"Local\"", vm.StatusMessage);
            Assert.Empty(await _service.LoadRunHistoryAsync(job.Id));
        }
        finally
        {
            await _service.DeleteJobAsync(job.Id);
        }
    }

    [Fact]
    public async Task NewJob_SavesWhatTheEditorAccepts()
    {
        var sessions = new ObservableCollection<DatabaseSession> { CreateSession("Local") };
        var vm = new AutomationViewModel(sessions, _service)
        {
            ShowJobEditor = editor =>
            {
                Assert.Equal(new[] { "Local" }, editor.AvailableConnections);
                editor.Name = "Nightly count";
                editor.ConnectionName = "Local";
                editor.SqlOrFilePath = "SELECT COUNT(*) FROM t";
                return Task.FromResult(editor.CanSave);
            }
        };

        await vm.NewJobCommand.ExecuteAsync(null);

        var job = Assert.Single(vm.Jobs);
        try
        {
            Assert.Same(job, vm.SelectedJob);
            var saved = (await _service.LoadJobsAsync()).Single(j => j.Id == job.Id);
            Assert.Equal("Nightly count", saved.Name);
            Assert.Equal("Local", saved.ConnectionName);
            Assert.Equal("SELECT COUNT(*) FROM t", saved.SqlOrFilePath);
        }
        finally
        {
            await _service.DeleteJobAsync(job.Id);
        }
    }

    [Fact]
    public async Task NewJob_CancelledEditor_AddsNothing()
    {
        var vm = new AutomationViewModel(new ObservableCollection<DatabaseSession>(), _service)
        {
            ShowJobEditor = _ => Task.FromResult(false)
        };

        await vm.NewJobCommand.ExecuteAsync(null);

        Assert.Empty(vm.Jobs);
    }

    [Fact]
    public void Editor_OffersOnlyRunnableTypes_AndRequiresConnectionAndBody()
    {
        var editor = new JobEditorDialogViewModel();

        Assert.Equal(new[] { JobType.RunQuery, JobType.Backup, JobType.DataTransfer }, editor.JobTypes);
        editor.Name = "x";
        Assert.False(editor.CanSave);
        editor.ConnectionName = "Local";
        Assert.False(editor.CanSave);
        editor.SqlOrFilePath = "SELECT 1";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void Editor_KeepsUnrunnableTypeOfExistingJob_AndWarns()
    {
        var editor = new JobEditorDialogViewModel();

        editor.LoadFromJob(new AutomationJob { Name = "old", Type = JobType.ExportResult });

        Assert.Contains(JobType.ExportResult, editor.JobTypes);
        Assert.Contains("cannot run yet", editor.TypeWarning);
        editor.Type = JobType.RunQuery;
        Assert.Null(editor.TypeWarning);
    }

    [Fact]
    public void Editor_PreservesScheduleFieldsItDoesNotEdit()
    {
        var job = new AutomationJob { Name = "a", CronExpression = "0 0 * * *", IsEnabled = false };
        var editor = new JobEditorDialogViewModel();
        editor.LoadFromJob(job);
        editor.Name = "b";

        editor.ApplyTo(job);

        Assert.Equal("b", job.Name);
        Assert.Equal("0 0 * * *", job.CronExpression);
        Assert.False(job.IsEnabled);
    }

    [Fact]
    public void Notice_StatesJobsRunOnlyOnDemand()
    {
        Assert.Contains("only when you click Run Now", AutomationViewModel.OnDemandNotice);
        Assert.Contains("not available", AutomationViewModel.OnDemandNotice);
    }

    private async Task<AutomationJob> SaveJobAsync(string connectionName, string sql)
    {
        var job = new AutomationJob
        {
            Name = "on-demand " + Guid.NewGuid().ToString("N"),
            Type = JobType.RunQuery,
            ConnectionName = connectionName,
            SqlOrFilePath = sql
        };
        await _service.SaveJobAsync(job);
        return job;
    }

    private static DatabaseSession CreateSession(string name)
    {
        var provider = new SqliteProvider();
        return new DatabaseSession(
            new ConnectionInfo { Name = name, Provider = provider.ProviderName, ConnectionString = "Data Source=:memory:" },
            provider);
    }
}
