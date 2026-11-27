using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Stores automation jobs and runs them on demand. There is no scheduler: a job runs only when
/// the user starts it against an open session. Scheduled execution is Tier 2 work (§2.10).
/// </summary>
public class AutomationService
{
    /// <summary>Job types that <see cref="ExecuteJobAsync"/> can run. Other types fail when run.</summary>
    public static IReadOnlyList<JobType> RunnableJobTypes { get; } = new[] { JobType.RunQuery, JobType.Backup, JobType.DataTransfer };

    private readonly DiagnosticsService _diagnostics;
    private readonly BackupService _backupService;
    private readonly string _dataDirectory;

    private string JobsFile => Path.Combine(_dataDirectory, "automation-jobs.json");
    private string HistoryFile(string jobId) => Path.Combine(_dataDirectory, $"automation-history-{jobId}.json");

    public AutomationService(
        DiagnosticsService? diagnostics = null,
        BackupService? backupService = null,
        string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? AppDataPaths.RootDirectory;
        _diagnostics = diagnostics ?? DiagnosticsService.Default;
        _backupService = backupService ?? new BackupService(_diagnostics);
    }

    private void EnsureDir() => Directory.CreateDirectory(_dataDirectory);

    public async Task<List<AutomationJob>> LoadJobsAsync()
    {
        EnsureDir();
        if (!File.Exists(JobsFile)) return new List<AutomationJob>();
        var json = await File.ReadAllTextAsync(JobsFile);
        return JsonSerializer.Deserialize<List<AutomationJob>>(json) ?? new List<AutomationJob>();
    }

    public async Task SaveJobAsync(AutomationJob job)
    {
        EnsureDir();
        var jobs = await LoadJobsAsync();
        var idx = jobs.FindIndex(j => j.Id == job.Id);
        if (idx >= 0) jobs[idx] = job;
        else jobs.Add(job);
        await File.WriteAllTextAsync(JobsFile, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task DeleteJobAsync(string id)
    {
        EnsureDir();
        var jobs = await LoadJobsAsync();
        jobs.RemoveAll(j => j.Id == id);
        await File.WriteAllTextAsync(JobsFile, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<List<JobRunRecord>> LoadRunHistoryAsync(string jobId)
    {
        EnsureDir();
        var file = HistoryFile(jobId);
        if (!File.Exists(file)) return new List<JobRunRecord>();
        var json = await File.ReadAllTextAsync(file);
        return JsonSerializer.Deserialize<List<JobRunRecord>>(json) ?? new List<JobRunRecord>();
    }

    public async Task AppendRunRecordAsync(JobRunRecord record)
    {
        EnsureDir();
        var history = await LoadRunHistoryAsync(record.JobId);
        history.Add(record);
        await File.WriteAllTextAsync(
            HistoryFile(record.JobId),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Runs a Data Transfer job: the saved profile it names (<see cref="AutomationJob.SqlOrFilePath"/>) on its connections
    /// among <paramref name="open"/>. It succeeds when every table is copied; otherwise the record names the tables that were not.
    /// </summary>
    public async Task<JobRunRecord> ExecuteTransferJobAsync(AutomationJob job, IReadOnlyList<DatabaseSession> open, CancellationToken cancellationToken)
    {
        var record = new JobRunRecord { JobId = job.Id, StartedAt = DateTimeOffset.UtcNow, Status = JobStatus.Running };
        try
        {
            var profile = await new DataTransferProfiles(_dataDirectory).FindAsync(job.SqlOrFilePath)
                          ?? throw new InvalidOperationException($"There is no transfer profile named \"{job.SqlOrFilePath}\".");
            var sides = await DataTransferProfiles.ResolveAsync(profile, open);
            if (sides.Error != null) throw new InvalidOperationException(sides.Error);
            var outcomes = await DataTransferProfiles.RunAsync(profile, sides.Source!, sides.Target!, cancellationToken: cancellationToken);
            record.RowsAffected = (int)Math.Min(int.MaxValue, outcomes.Sum(o => o.RowsCopied));
            var unfinished = outcomes.Where(o => o.Status is DataTransferTableStatus.Failed or DataTransferTableStatus.Canceled).ToList();
            record.Status = cancellationToken.IsCancellationRequested ? JobStatus.Cancelled
                : unfinished.Count == 0 ? JobStatus.Succeeded : JobStatus.Failed;
            if (unfinished.Count > 0)
                record.ErrorMessage = string.Join("; ", unfinished.Select(o => $"{ObjectNames.SchemaQualified(o.Table)}: {o.Status} {o.Diagnostic}".Trim()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            record.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            record.Status = JobStatus.Failed;
            record.ErrorMessage = DiagnosticRedactor.Redact(ex.Message);
        }
        record.CompletedAt = DateTimeOffset.UtcNow;
        return record;
    }

    public async Task<JobRunRecord> ExecuteJobAsync(AutomationJob job, DatabaseSession session, CancellationToken cancellationToken)
    {
        var record = new JobRunRecord
        {
            JobId = job.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = JobStatus.Running
        };
        var operation = _diagnostics.BeginOperation(
            "automation",
            job.Type.ToString(),
            session.ProviderName,
            session.Name);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (job.Type)
            {
                case JobType.RunQuery:
                    if (string.IsNullOrWhiteSpace(job.SqlOrFilePath))
                        throw new InvalidOperationException("Automation query is empty.");

                    var result = await session.ExecuteQueryAsync(job.SqlOrFilePath,
                        new QueryExecutionOptions { CancellationToken = cancellationToken });
                    if (result.Error?.IsCanceled == true || cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    if (result.HasError)
                        throw new InvalidOperationException(result.ErrorMessage ?? "Automation query failed.");

                    record.RowsAffected = result.RecordsAffected > 0 ? result.RecordsAffected : result.ObjectRows.Count;
                    break;

                case JobType.Backup:
                    if (string.IsNullOrWhiteSpace(job.SqlOrFilePath))
                        throw new InvalidOperationException("Automation backup output path is empty.");

                    await _backupService.BackupAsync(session, job.SqlOrFilePath, new Progress<string>(), cancellationToken);
                    break;

                case JobType.DataTransfer:
                    throw new InvalidOperationException("Data Transfer jobs run through ExecuteTransferJobAsync.");

                case JobType.ExportResult:
                case JobType.ImportCsv:
                    throw new NotSupportedException($"Automated {job.Type} jobs are not implemented yet; the job was not reported as successful.");
            }

            record.Status = JobStatus.Succeeded;
            _diagnostics.Record(
                operation,
                DiagnosticSeverity.Information,
                "Automation job completed.",
                new Dictionary<string, string?>
                {
                    ["jobId"] = job.Id,
                    ["status"] = record.Status.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            record.Status = JobStatus.Cancelled;
            _diagnostics.Record(
                operation,
                DiagnosticSeverity.Warning,
                "Automation job was cancelled.",
                new Dictionary<string, string?> { ["jobId"] = job.Id });
        }
        catch (Exception ex)
        {
            record.Status = JobStatus.Failed;
            record.ErrorMessage = DiagnosticRedactor.Redact(ex.Message);
            _diagnostics.RecordFailure(
                operation,
                ex,
                "Automation job failed.",
                new Dictionary<string, string?>
                {
                    ["jobId"] = job.Id,
                    ["status"] = record.Status.ToString()
                });
        }

        record.CompletedAt = DateTimeOffset.UtcNow;
        return record;
    }
}
