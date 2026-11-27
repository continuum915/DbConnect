using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class DataCompareViewModel : ViewModelBase
{
    private readonly DataCompareService _svc = new();
    private readonly List<DataDiffRow> _allRows = new();
    private readonly List<RowDiff> _allDiffs = new();
    private List<string> _primaryKeyColumns = new();

    [ObservableProperty] private DatabaseSession? _sourceSession;
    [ObservableProperty] private DatabaseSession? _targetSession;
    [ObservableProperty] private string _sourceTableName = string.Empty;
    [ObservableProperty] private string _targetTableName = string.Empty;
    [ObservableProperty] private bool _showIdentical;
    [ObservableProperty] private string _compareStatus = string.Empty;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _whereFilter = string.Empty;
    [ObservableProperty] private int _maxRows = 100_000;
    [ObservableProperty] private ConflictResolutionPolicy _conflictPolicy = ConflictResolutionPolicy.SourceWins;
    [ObservableProperty] private DataSyncProgress _progress = new();
    [ObservableProperty] private bool _isTruncated;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<DataDiffRow> DiffRows { get; } = new();
    public IReadOnlyList<ConflictResolutionPolicy> ConflictPolicies { get; } = Enum.GetValues<ConflictResolutionPolicy>();
    public MaskingRulesViewModel MaskingRules { get; } = new();

    /// <summary>Wire this to open a script in a new query tab.</summary>
    public Action<string>? RequestOpenInTab { get; set; }

    public DataCompareViewModel(ObservableCollection<DatabaseSession> availableSessions)
    {
        Title = "Data Compare";
        AvailableSessions = availableSessions;
        Apply = new ApplySyncViewModel(PreviewSyncAsync, () => RunCompareCommand.ExecuteAsync(null))
        {
            OpenInTab = script => RequestOpenInTab?.Invoke(script)
        };
    }

    /// <summary>Preview → Apply of the ticked differences.</summary>
    public ApplySyncViewModel Apply { get; }

    /// <summary>
    /// The ticked differences as a plan for the target table. Only "source wins" writes anything; a comparison cut short
    /// by Max Rows can apply only the rows it compared, and says so.
    /// </summary>
    private async Task<SyncPreview> PreviewSyncAsync()
    {
        if (TargetSession is not { } target || string.IsNullOrWhiteSpace(TargetTableName))
            return new(null, null, "Compare two tables first.");
        if (ConflictPolicy != ConflictResolutionPolicy.SourceWins)
            return new(null, null, $"Conflict policy {ConflictPolicy} writes nothing to the target; choose SourceWins to apply.");
        var ticked = _allRows.Where(r => r.IsSelected && r.Status != RowDiffStatus.Identical && r.Diff != null).Select(r => r.Diff!).ToList();
        if (ticked.Count == 0) return new(null, null, "Tick the differences to apply.");
        if (_primaryKeyColumns.Count == 0) return new(null, null, "The table has no primary key, so its rows cannot be matched to apply them.");
        var plan = DataSyncPlanner.Build(target, TargetTableName, _primaryKeyColumns, ticked, await ColumnTypesAsync(target, TargetTableName));
        return new(target, IsTruncated
            ? plan with { Notes = [$"The comparison stopped at Max Rows ({MaxRows}); only the rows it compared are applied."] }
            : plan);
    }

    /// <summary>The target's declared column types, so an engine that wants typed NULLs gets them; none when they cannot be read.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> ColumnTypesAsync(DatabaseSession target, string table)
    {
        if (target.Provider is not ITableDesignSource designs) return null;
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var design = await designs.GetTableDesignAsync(target.ConnectionString,
                new DatabaseObjectIdentity(null, parts.Length > 1 ? parts[^2] : null, parts[^1], SchemaNodeType.Table));
            return design.Columns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private void OnRowTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataDiffRow.IsSelected)) Apply.Invalidate();
    }

    // Designer constructor
    public DataCompareViewModel() : this(new ObservableCollection<DatabaseSession>()) { }

    partial void OnShowIdenticalChanged(bool value) => RefreshVisibleRows();

    [RelayCommand]
    private async Task RunCompare()
    {
        if (SourceSession == null || TargetSession == null ||
            string.IsNullOrWhiteSpace(SourceTableName) || string.IsNullOrWhiteSpace(TargetTableName))
        {
            CompareStatus = "Select sessions and table names.";
            return;
        }

        IsComparing = true;
        CompareStatus = "Comparing...";
        Progress = new DataSyncProgress();
        IsTruncated = false;
        foreach (var old in _allRows) old.PropertyChanged -= OnRowTicked;
        _allRows.Clear();
        _allDiffs.Clear();
        Apply.Invalidate();
        _primaryKeyColumns = new List<string>();
        DiffRows.Clear();

        var request = new DataCompareRequest
        {
            SourceSession = SourceSession,
            TargetSession = TargetSession,
            SourceTable = SourceTableName,
            TargetTable = TargetTableName,
            WhereFilter = string.IsNullOrWhiteSpace(WhereFilter) ? null : WhereFilter,
            MaxRows = MaxRows,
            ConflictPolicy = ConflictPolicy
        };

        var progressReporter = new Progress<DataSyncProgress>(UpdateProgress);

        try
        {
            await foreach (var chunk in _svc.CompareAsync(request, progressReporter))
            {
                foreach (var diff in chunk.Differences)
                {
                    _allDiffs.Add(diff);
                    var row = ToDisplayRow(diff);
                    row.Diff = diff;
                    row.PropertyChanged += OnRowTicked;
                    _allRows.Add(row);
                    if (ShowIdentical || row.Status != RowDiffStatus.Identical)
                        DiffRows.Add(row);
                }

                Progress = chunk.Progress;
                IsTruncated = chunk.Progress.IsTruncated;
            }

            _primaryKeyColumns = request.PrimaryKeyColumns.Count > 0
                ? new List<string>(request.PrimaryKeyColumns)
                : await TablePrimaryKeys.ResolveAsync(SourceSession, SourceTableName);

            CompareStatus = BuildStatusText(Progress, isComplete: true);
        }
        catch (Exception ex)
        {
            CompareStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    private void UpdateProgress(DataSyncProgress progress)
    {
        Progress = progress;
        IsTruncated = progress.IsTruncated;
        CompareStatus = BuildStatusText(progress, isComplete: false);
    }

    private string BuildStatusText(DataSyncProgress progress, bool isComplete)
    {
        var prefix = isComplete ? "Done" : "Comparing";
        var suffix = progress.IsTruncated ? " Limit reached; results truncated." : string.Empty;
        return $"{prefix}. {progress.RowsCompared} row(s) compared, {progress.DifferencesFound} difference(s) found.{suffix}";
    }

    private void RefreshVisibleRows()
    {
        DiffRows.Clear();
        foreach (var row in ShowIdentical ? _allRows : _allRows.Where(r => r.Status != RowDiffStatus.Identical))
            DiffRows.Add(row);
    }

    private static DataDiffRow ToDisplayRow(RowDiff diff)
    {
        return diff.Kind switch
        {
            RowDiffKind.OnlyInSource => new DataDiffRow
            {
                Status = RowDiffStatus.OnlyInSource,
                SourceValues = ToStringDictionary(diff.SourceRow)
            },
            RowDiffKind.OnlyInTarget => new DataDiffRow
            {
                Status = RowDiffStatus.OnlyInTarget,
                TargetValues = ToStringDictionary(diff.TargetRow)
            },
            RowDiffKind.Modified => new DataDiffRow
            {
                Status = RowDiffStatus.Modified,
                SourceValues = ToStringDictionary(diff.SourceRow),
                TargetValues = ToStringDictionary(diff.TargetRow),
                ChangedColumns = diff.SourceRow.Keys.Union(diff.TargetRow.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(column =>
                    {
                        diff.SourceRow.TryGetValue(column, out var sourceValue);
                        diff.TargetRow.TryGetValue(column, out var targetValue);
                        return !Equals(sourceValue, targetValue);
                    })
                    .ToList()
            },
            _ => new DataDiffRow
            {
                Status = RowDiffStatus.Identical,
                SourceValues = ToStringDictionary(diff.SourceRow),
                TargetValues = ToStringDictionary(diff.TargetRow)
            }
        };
    }

    private static Dictionary<string, string?> ToStringDictionary(Dictionary<string, object?> row)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in row)
            result[key] = value?.ToString();

        return result;
    }
}
