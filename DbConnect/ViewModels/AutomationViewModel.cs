using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Automation jobs run only on demand, against the open session whose name matches the job's
/// connection. There is no scheduler (Tier 2 §2.10), and the UI says so.
/// </summary>
public partial class AutomationViewModel : ViewModelBase
{
    public const string OnDemandNotice =
        "Jobs run only when you click Run Now, against an open connection with the job's connection name. Scheduled runs are not available yet.";

    private readonly AutomationService _service;
    private readonly ObservableCollection<DatabaseSession> _sessions;
    private CancellationTokenSource? _cts;

    public AutomationViewModel(ObservableCollection<DatabaseSession> sessions, AutomationService? service = null)
    {
        _sessions = sessions;
        _service = service ?? new AutomationService();
    }

    public override string Title => "Automation";

    /// <summary>Shows the job editor; returns true when the user accepts. Set by the view.</summary>
    public Func<JobEditorDialogViewModel, Task<bool>>? ShowJobEditor { get; set; }

    [ObservableProperty]
    private ObservableCollection<AutomationJob> _jobs = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditJobCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteJobCommand))]
    private AutomationJob? _selectedJob;

    [ObservableProperty]
    private ObservableCollection<JobRunRecord> _runHistory = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedJobChanged(AutomationJob? value)
    {
        RunHistory.Clear();
        StatusMessage = null;
        if (value != null)
            _ = LoadHistoryAsync(value.Id);
    }

    private async Task LoadHistoryAsync(string jobId)
    {
        var history = await _service.LoadRunHistoryAsync(jobId);
        if (SelectedJob?.Id != jobId) return;
        RunHistory.Clear();
        foreach (var r in history.OrderByDescending(r => r.StartedAt)) RunHistory.Add(r);
    }

    [RelayCommand]
    private async Task NewJob()
    {
        var job = new AutomationJob { Name = "New Job" };
        if (!await EditAsync(job)) return;
        await _service.SaveJobAsync(job);
        Jobs.Add(job);
        SelectedJob = job;
    }

    private bool HasSelection() => SelectedJob != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditJob()
    {
        var job = SelectedJob;
        if (job == null || !await EditAsync(job)) return;
        await _service.SaveJobAsync(job);
        var index = Jobs.IndexOf(job);
        if (index >= 0) Jobs[index] = job;
        // AutomationJob is a plain DTO; reselect so bindings to SelectedJob refresh.
        SelectedJob = null;
        SelectedJob = job;
    }

    private async Task<bool> EditAsync(AutomationJob job)
    {
        if (ShowJobEditor == null) return false;
        var editor = new JobEditorDialogViewModel();
        foreach (var name in _sessions.Select(s => s.Name).Distinct(StringComparer.Ordinal))
            editor.AvailableConnections.Add(name);
        editor.LoadFromJob(job);
        if (!await ShowJobEditor(editor)) return false;
        editor.ApplyTo(job);
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteJob()
    {
        if (SelectedJob == null) return;
        await _service.DeleteJobAsync(SelectedJob.Id);
        Jobs.Remove(SelectedJob);
        SelectedJob = null;
    }

    private bool CanRunNow() => SelectedJob != null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunNow()
    {
        var job = SelectedJob;
        if (job == null) return;
        if (job.Type == JobType.DataTransfer)
        {
            await RunTransferAsync(job);
            return;
        }
        var session = _sessions.FirstOrDefault(s => string.Equals(s.Name, job.ConnectionName, StringComparison.Ordinal));
        if (session == null)
        {
            StatusMessage = string.IsNullOrWhiteSpace(job.ConnectionName)
                ? "This job has no connection. Edit the job and choose one."
                : $"Open the connection \"{job.ConnectionName}\" first, then run the job again.";
            return;
        }

        IsRunning = true;
        StatusMessage = $"Running \"{job.Name}\" on {session.Name}…";
        _cts = new CancellationTokenSource();
        try
        {
            var record = await _service.ExecuteJobAsync(job, session, _cts.Token);
            await _service.AppendRunRecordAsync(record);
            if (SelectedJob?.Id == job.Id)
                RunHistory.Insert(0, record);
            StatusMessage = record.Status switch
            {
                JobStatus.Succeeded => $"\"{job.Name}\" succeeded.",
                JobStatus.Cancelled => $"\"{job.Name}\" was cancelled.",
                _ => $"\"{job.Name}\" failed: {record.ErrorMessage}"
            };
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>A Data Transfer job finds its profile's two connections among the open ones itself.</summary>
    private async Task RunTransferAsync(AutomationJob job)
    {
        IsRunning = true;
        StatusMessage = $"Running \"{job.Name}\" (transfer profile \"{job.SqlOrFilePath}\")…";
        _cts = new CancellationTokenSource();
        try
        {
            var record = await _service.ExecuteTransferJobAsync(job, _sessions.ToList(), _cts.Token);
            await _service.AppendRunRecordAsync(record);
            if (SelectedJob?.Id == job.Id) RunHistory.Insert(0, record);
            StatusMessage = record.Status switch
            {
                JobStatus.Succeeded => $"\"{job.Name}\" succeeded: {record.RowsAffected} rows transferred.",
                JobStatus.Cancelled => $"\"{job.Name}\" was cancelled.",
                _ => $"\"{job.Name}\" failed: {record.ErrorMessage}"
            };
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancelRun() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun() => _cts?.Cancel();

    [RelayCommand]
    public async Task LoadJobs()
    {
        var selectedId = SelectedJob?.Id;
        var jobs = await _service.LoadJobsAsync();
        Jobs.Clear();
        foreach (var j in jobs) Jobs.Add(j);
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == selectedId);
    }
}
