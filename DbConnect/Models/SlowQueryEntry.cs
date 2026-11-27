using System;

namespace DbConnect.Models;

public class SlowQueryEntry
{
    public string ConnectionName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public TimeSpan Duration { get; set; }
    public int RowsAffected { get; set; }
    public bool IsError { get; set; }
    public string QueryText { get; set; } = string.Empty;
    /// <summary>True when <see cref="QueryText"/> is only a preview of an oversized script, so it must not be run.</summary>
    public bool IsTruncated { get; set; }
    /// <summary>Normalized query with literals stripped, used for grouping.</summary>
    public string Fingerprint { get; set; } = string.Empty;
}

public class SlowQueryAggregate
{
    public string Fingerprint { get; set; } = string.Empty;
    public string SampleQuery { get; set; } = string.Empty;
    public bool IsTruncated { get; set; }
    public int Count { get; set; }
    public TimeSpan AvgDuration { get; set; }
    public TimeSpan MaxDuration { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string ConnectionName { get; set; } = string.Empty;
}
