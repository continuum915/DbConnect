using System.Collections.Generic;

namespace DbConnect.Models;

public enum RowDiffStatus { OnlyInSource, Modified, OnlyInTarget, Identical }

public partial class DataDiffRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>Whether Apply includes this difference; every difference starts ticked.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected = true;

    /// <summary>The comparison's own row values, which Apply writes; null for rows built without them.</summary>
    public RowDiff? Diff { get; set; }

    public RowDiffStatus Status { get; set; }
    public Dictionary<string, string?> SourceValues { get; set; } = new();
    public Dictionary<string, string?> TargetValues { get; set; } = new();
    public List<string> ChangedColumns { get; set; } = new();
}

public class DataDiffResult
{
    public List<DataDiffRow> Rows { get; set; } = new();
    public string SourceTable { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public List<string> PrimaryKeyColumns { get; set; } = new();
    public int TotalCompared { get; set; }
    public bool IsTruncated { get; set; }
    public string? ErrorMessage { get; set; }
}
