using System;

namespace DbConnect.Models;

/// <summary>New types are added at the end: saved jobs store the number.</summary>
public enum JobType { RunQuery, ExportResult, ImportCsv, Backup, DataTransfer }
public enum JobStatus { Pending, Running, Succeeded, Failed, Cancelled }

public class AutomationJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public JobType Type { get; set; }
    public string ConnectionName { get; set; } = string.Empty;
    public string SqlOrFilePath { get; set; } = string.Empty;

    // Kept so saved jobs round-trip unchanged. Nothing reads them: there is no scheduler yet
    // (Tier 2 §2.10), and jobs run only on demand.
    public string CronExpression { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class JobRunRecord
{
    public string JobId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public JobStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public int RowsAffected { get; set; }
}
