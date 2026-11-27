using System.Collections.Generic;
using DbConnect.Services;

namespace DbConnect.Models;

public enum ConflictResolutionPolicy
{
    SourceWins,
    TargetWins,
    ManualReview
}

public enum RowDiffKind
{
    Equal,
    OnlyInSource,
    OnlyInTarget,
    Modified
}

public class DataCompareRequest
{
    public DatabaseSession SourceSession { get; set; } = null!;
    public DatabaseSession TargetSession { get; set; } = null!;
    public string SourceTable { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public List<string> PrimaryKeyColumns { get; set; } = new();
    public int MaxRows { get; set; } = 100_000;
    public string? WhereFilter { get; set; }
    public ConflictResolutionPolicy ConflictPolicy { get; set; } = ConflictResolutionPolicy.SourceWins;
}

public class DataSyncProgress
{
    public long RowsCompared { get; set; }
    public long DifferencesFound { get; set; }
    public bool IsTruncated { get; set; }
    public string? CurrentPrimaryKey { get; set; }
}

public class DataCompareChunk
{
    public List<RowDiff> Differences { get; set; } = new();
    public DataSyncProgress Progress { get; set; } = new();
    public bool IsLast { get; set; }
}

public class RowDiff
{
    public RowDiffKind Kind { get; set; }
    public Dictionary<string, object?> SourceRow { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> TargetRow { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    public string PrimaryKeyValue { get; set; } = string.Empty;
}
