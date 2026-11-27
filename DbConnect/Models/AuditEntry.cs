using System;

namespace DbConnect.Models;

public class AuditEntry
{
    public string ConnectionName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public long DurationMs { get; set; }
    public int RowsAffected { get; set; }
    public bool IsError { get; set; }
    public string QueryFingerprint { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
}
