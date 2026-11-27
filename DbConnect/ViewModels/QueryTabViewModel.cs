using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel : ViewModelBase
{
    private readonly Dictionary<DatabaseSession, SchemaIndex> _sessionIndexes = new();
    private readonly QueryWorkspaceService? _workspaceService;
    private readonly SlowQueryLogService? _slowQueryLogService;
    private readonly List<QueryResultDisplayRow> _allDisplayRows = [];
    private CancellationTokenSource? _executionCts;
    private readonly SemaphoreSlim _dbSessionOpenGate = new(1, 1);
    private readonly object _dbSessionLock = new();
    private IDbSession? _dbSession;
    private int _dbSessionGeneration;
    private DatabaseSession? _acceptedSession;

    /// <summary>RAIN-P2-16 — Edits the current result in place when it maps to one keyed table; a commit redraws the result.</summary>
    public QueryResultEditorViewModel ResultEditor => field ??=
        new(() => GetOrOpenDbSessionAsync(), () => { RefreshTransactionState(); RebuildDisplayedResults(CurrentResult); });

    /// <summary>RAIN-P2-17 — Column chooser and copy options for the results grid.</summary>
    public ResultGridOptionsViewModel ResultGrid => field ??= new(row => ResultEditor.CurrentValuesOf(row));

    [ObservableProperty]
    private string _selectedQueryText = string.Empty;

    /// <summary>Editor caret offset; Run Selection with nothing selected runs the statement at this offset.</summary>
    public int CaretOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary))]
    [NotifyPropertyChangedFor(nameof(StructuredErrorSummary))]
    [NotifyPropertyChangedFor(nameof(HasStructuredError))]
    [NotifyPropertyChangedFor(nameof(BatchStatementSummary))]
    [NotifyPropertyChangedFor(nameof(MessagesRows))]
    [NotifyPropertyChangedFor(nameof(CanCopyResults))]
    [NotifyPropertyChangedFor(nameof(CanExportResults), nameof(ExportScopeExplanation), nameof(FullQueryUnavailableReason))]
    [NotifyPropertyChangedFor(nameof(TruncationSummary))]
    private QueryResult? _currentResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanChangeCommitMode))]
    [NotifyPropertyChangedFor(nameof(CanChangeConnection))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    private bool _isExecuting;

    /// <summary>
    /// Manual-commit mode: statements run inside a transaction that stays open until the user
    /// commits or rolls back. Auto-commit (the default) leaves each statement to commit itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeCommitMode), nameof(AutoCommit))]
    private bool _isManualCommit;

    /// <summary>True while this tab's session has an open transaction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransactionStatusText), nameof(ExportScopeExplanation), nameof(FullQueryUnavailableReason))]
    [NotifyPropertyChangedFor(nameof(CanChangeCommitMode))]
    [NotifyPropertyChangedFor(nameof(CanChangeConnection))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    private bool _isInTransaction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsAnalyzeExplain))]
    [NotifyPropertyChangedFor(nameof(ExplainButtonToolTip))]
    private SchemaIndex? _currentIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsAnalyzeExplain))]
    [NotifyPropertyChangedFor(nameof(ExplainButtonToolTip), nameof(ExportScopeExplanation), nameof(FullQueryUnavailableReason))]
    private DatabaseSession? _selectedSession;

    [ObservableProperty]
    private QueryResult? _explainResult;

    [ObservableProperty]
    private ExplainPlanViewModel? _explainPlan;

    [ObservableProperty]
    private bool _showExplainPane;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isExplaining;

    [ObservableProperty]
    private bool _allowAnalyze;

    [ObservableProperty]
    private bool _showAnalyzeBanner;

    [ObservableProperty]
    private ObservableCollection<QueryHistoryEntry> _recentQueries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedHistoryCommand))]
    private QueryHistoryEntry? _selectedHistoryEntry;

    [ObservableProperty]
    private ObservableCollection<SavedQueryDefinition> _savedQueries = [];

    [ObservableProperty]
    private SavedQueryDefinition? _selectedSavedQuery;

    [ObservableProperty]
    private ObservableCollection<SqlSnippetDefinition> _availableSnippets = [];

    [ObservableProperty]
    private SqlSnippetDefinition? _selectedSnippet;

    [ObservableProperty]
    private string _saveQueryName = string.Empty;

    [ObservableProperty]
    private int _timeoutSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary))]
    [NotifyPropertyChangedFor(nameof(TruncationSummary))]
    private int _resultRowCap = 1000;

    [ObservableProperty]
    private string _resultFilterText = string.Empty;

    [ObservableProperty]
    private string? _resultSortColumn;

    [ObservableProperty]
    private bool _resultSortDescending;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private ObservableCollection<string> _resultColumns = [];

    [ObservableProperty]
    private ObservableCollection<IReadOnlyList<string>> _displayResultRows = [];

    [ObservableProperty]
    private ObservableCollection<QueryResult> _results = [];

    [ObservableProperty]
    private QueryResult? _selectedResult;

    [ObservableProperty]
    private bool _hasMultipleResults;

    private bool _hasSeenAnalyzeWarning;
    private bool _isApplyingExecutionResults;

    public override FluentIcons.Common.Symbol? TabIcon => FluentIcons.Common.Symbol.Code;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public IReadOnlyList<int> TimeoutChoices { get; } = [15, 30, 60, 120, 300];
    public IReadOnlyList<int> ResultRowCapChoices { get; } = [100, 250, 500, 1000, 5000, 50_000];
    public bool SupportsAnalyzeExplain => SelectedSession?.Capabilities.SupportsAnalyzeExplain == true;
    public bool CanCancel => IsExecuting || IsExplaining;
    public bool HasStructuredError => CurrentResult?.HasError == true;
    public bool CanCopyResults => CurrentResult?.HasResultSet == true;
    public bool CanExportResults => CurrentResult?.HasResultSet == true;
    public string ExplainButtonToolTip => SupportsAnalyzeExplain
        ? "Show execution plan. ANALYZE is available for SELECT and WITH statements on this connection."
        : "Show execution plan.";

    public string ResultSummary
    {
        get
        {
            if (CurrentResult == null)
                return $"Timeout: {TimeoutSeconds}s - Cap: {ResultRowCap:N0} rows";

            var scope = CurrentResult.ExecutionScope switch
            {
                QueryExecutionScope.Selection => "Selection",
                QueryExecutionScope.Explain => "Explain",
                _ => "Editor"
            };

            var statementPart = CurrentResult.StatementCount > 1
                ? $"{CurrentResult.StatementCount} statements"
                : "1 statement";

            var durationPart = $"{CurrentResult.Duration.TotalMilliseconds:N0} ms";

            var resultPart = CurrentResult.HasError
                ? "Error"
                : CurrentResult.HasResultSet
                    ? $"{CurrentResult.ObjectRows.Count:N0} row(s)"
                    : CurrentResult.RecordsAffected >= 0
                        ? $"{CurrentResult.RecordsAffected:N0} row(s) affected"
                        : "Statement executed";

            var routedStatementPart = CurrentResult.ReturnedStatementIndex >= 0
                ? $" - result from statement #{CurrentResult.ReturnedStatementIndex + 1}"
                : string.Empty;

            var truncatedPart = CurrentResult.IsTruncated
                ? $" - capped at {CurrentResult.RowCap:N0} rows"
                : string.Empty;

            return $"{scope} - {statementPart} - {durationPart} - {resultPart}{routedStatementPart}{truncatedPart}";
        }
    }

    public string StructuredErrorSummary => CurrentResult?.StructuredErrorSummary ?? string.Empty;

    public string TruncationSummary =>
        CurrentResult?.IsTruncated == true
            ? $"Results were capped at {CurrentResult.RowCap:N0} rows to keep the grid responsive."
            : string.Empty;

    public event Action<string>? InsertTextRequested;

    /// <summary>Raises <see cref="InsertTextRequested"/> so callers outside the VM can trigger text insertion.</summary>
    public void RequestInsertText(string text) => InsertTextRequested?.Invoke(text);

    public QueryTabViewModel(
        string title = "New Query",
        ObservableCollection<DatabaseSession>? availableSessions = null,
        QueryWorkspaceService? workspaceService = null,
        SlowQueryLogService? slowQueryLogService = null)
    {
        Title = title;
        AvailableSessions = availableSessions ?? new ObservableCollection<DatabaseSession>();
        _workspaceService = workspaceService;
        _slowQueryLogService = slowQueryLogService;
        SelectedSession = AvailableSessions.Count > 0 ? AvailableSessions[0] : null;
        IsManualCommit = AppPreferences.Execution.NewTabsManualCommit;
        LoadProviderSnippets();
        _ = InitializeWorkspaceAsync();
    }

    public QueryTabViewModel()
    {
        AvailableSessions = new ObservableCollection<DatabaseSession>();
        LoadProviderSnippets();
    }

    partial void OnAllowAnalyzeChanged(bool value)
    {
        if (value && !_hasSeenAnalyzeWarning)
        {
            ShowAnalyzeBanner = true;
            _hasSeenAnalyzeWarning = true;
        }
    }

    /// <summary>True while this tab holds an open database session.</summary>
    public bool HasDbSession => _dbSession != null;

    /// <summary>
    /// The tab's own database session, opened on first use. Tabs never share one,
    /// even when they target the same connection.
    /// </summary>
    public async Task<IDbSession> GetOrOpenDbSessionAsync(CancellationToken cancellationToken = default)
    {
        // One open at a time, so overlapping first uses share a single session.
        await _dbSessionOpenGate.WaitAsync(cancellationToken);
        try
        {
            if (_dbSession != null)
                return _dbSession;

            var connection = SelectedSession ?? throw new InvalidOperationException("No active connection selected.");
            int generation;
            lock (_dbSessionLock)
                generation = _dbSessionGeneration;

            var opened = await SessionSetup.OpenAsync(connection, cancellationToken);

            // Any release since the open began (a connection change, even one that
            // changed back) bumps the generation and makes this session stale.
            lock (_dbSessionLock)
            {
                if (generation == _dbSessionGeneration)
                {
                    _dbSession = opened;
                    _metadataSessionOwner = connection;
                    return opened;
                }
            }

            await opened.DisposeAsync();
            throw new InvalidOperationException("The selected connection changed while the session was opening.");
        }
        finally
        {
            _dbSessionOpenGate.Release();
        }
    }

    /// <summary>The commit mode can only change between transactions: leaving manual mode would silently end the open one.</summary>
    public bool CanChangeCommitMode => !IsInTransaction && !IsExecuting;

    /// <summary>The connection can only change between transactions: switching releases the session, which would roll the open one back.</summary>
    public bool CanChangeConnection => !IsInTransaction && !IsExecuting && !ResultEditor.HasChanges;

    private bool CanEndTransaction => IsInTransaction && !IsExecuting;

    partial void OnIsManualCommitChanged(bool value)
    {
        if (!value && IsInTransaction)
            IsManualCommit = true;
    }

    /// <summary>Re-reads the session's transaction state; call after anything that may have begun or ended one.</summary>
    public void RefreshTransactionState()
    {
        IsInTransaction = _dbSession?.IsInTransaction == true;
        OnPropertyChanged(nameof(TransactionStatusText));
    }

    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task CommitAsync() => EndTransactionAsync(commit: true);

    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task RollbackAsync() => EndTransactionAsync(commit: false);

    partial void OnSelectedSessionChanged(DatabaseSession? value)
    {
        // Undoing a refused change lands here again with the connection the tab is already on.
        if (ReferenceEquals(value, _acceptedSession))
            return;

        // Switching would release the session and silently roll back the open transaction, or strand the
        // result's pending edits, so refuse it. A connection that is going away is the exception.
        if ((IsInTransaction || IsExecuting || ResultEditor.HasChanges) && _acceptedSession != null && AvailableSessions.Contains(_acceptedSession))
        {
            SelectedSession = _acceptedSession;
            return;
        }

        _acceptedSession = value;
        if (value?.Connection.Options?.CommandTimeoutSeconds is > 0 and var seconds) TimeoutSeconds = seconds;
        _ = ReleaseDbSessionAsync();
        ResultEditor.Load(CurrentResult, _allDisplayRows.ToList(), value); // the result stays, but not editable

        if (value != null && _sessionIndexes.TryGetValue(value, out var index))
            CurrentIndex = index;

        if (value?.Capabilities.SupportsAnalyzeExplain != true)
        {
            AllowAnalyze = false;
            ShowAnalyzeBanner = false;
        }

        LoadProviderSnippets();
        _ = LoadHistoryAsync();
    }

    partial void OnTimeoutSecondsChanged(int value)
    {
        if (value <= 0)
            TimeoutSeconds = 30;
    }

    partial void OnResultRowCapChanged(int value)
    {
        if (value <= 0)
            ResultRowCap = 1000;
    }

    partial void OnCurrentResultChanged(QueryResult? value)
    {
        if (!_isApplyingExecutionResults)
        {
            if (HasMultipleResults)
            {
                if (value != null && Results.Contains(value) && !ReferenceEquals(SelectedResult, value))
                    SelectedResult = value;
            }
            else
            {
                if (value == null)
                {
                    if (Results.Count > 0)
                        Results = [];
                    if (SelectedResult != null)
                        SelectedResult = null;
                }
                else
                {
                    if (Results.Count != 1 || !ReferenceEquals(Results[0], value))
                        Results = [value];
                    if (!ReferenceEquals(SelectedResult, value))
                        SelectedResult = value;
                }

                HasMultipleResults = false;
            }
        }

        RebuildDisplayedResults(value);
        if (ShowChartPane && value?.HasResultSet == true)
            _chart.UpdateChart(value);
    }

    partial void OnSelectedResultChanged(QueryResult? value)
    {
        if (_isApplyingExecutionResults)
            return;

        if (!ReferenceEquals(CurrentResult, value))
            CurrentResult = value;
    }

    partial void OnResultFilterTextChanged(string value)
    {
        ApplyDisplayResultView();
    }

    partial void OnResultSortColumnChanged(string? value)
    {
        ApplyDisplayResultView();
    }

    partial void OnResultSortDescendingChanged(bool value)
    {
        ApplyDisplayResultView();
    }

    public void SetSessionIndex(DatabaseSession session, SchemaIndex index)
    {
        _sessionIndexes[session] = index;
        if (SelectedSession == session)
            CurrentIndex = index;
    }

    public void RemoveSession(DatabaseSession session)
    {
        _sessionIndexes.Remove(session);
        if (SelectedSession == session)
        {
            SelectedSession = AvailableSessions.FirstOrDefault(s => s != session);
            if (SelectedSession == null)
                CurrentIndex = null;
        }
    }

    public CancellationToken BeginExecution()
    {
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _executionCts = new CancellationTokenSource();
        return _executionCts.Token;
    }

    public void EndExecution()
    {
        _executionCts?.Dispose();
        _executionCts = null;
    }

    public void SetExecutionResults(IEnumerable<QueryResult> results, QueryResult? currentResult = null)
    {
        var resultList = results.ToList();
        var selectedResult = currentResult != null && resultList.Contains(currentResult)
            ? currentResult
            : resultList.FirstOrDefault();

        _isApplyingExecutionResults = true;
        try
        {
            Results = new ObservableCollection<QueryResult>(resultList);
            HasMultipleResults = resultList.Count > 1;
            SelectedResult = selectedResult;
            CurrentResult = currentResult ?? selectedResult;
        }
        finally
        {
            _isApplyingExecutionResults = false;
        }

        RebuildDisplayedResults(CurrentResult);
    }

    public async Task RegisterExecutedQueryAsync(string sql, QueryExecutionScope scope, QueryResult result)
    {
        if (SelectedSession == null || string.IsNullOrWhiteSpace(sql))
            return;

        if (_workspaceService != null)
        {
            var connection = SelectedSession.Connection;
            var entry = new QueryHistoryEntry { Sql = sql, ProviderName = SelectedSession.ProviderName, Scope = scope };
            await Task.Run(() => _workspaceService.AddHistoryEntryAsync(connection, entry));
            await LoadHistoryAsync();
        }

        // Log slow queries
        if (_slowQueryLogService != null
            && result.Duration.TotalMilliseconds >= _slowQueryLogService.SlowQueryThresholdMs)
            _ = _slowQueryLogService.LogExecutedAsync(SelectedSession.Name, result.Duration,
                Math.Max(0, result.RecordsAffected), result.HasError, sql);
    }

    public string? BuildCsvExport()
    {
        if (ResultColumns.Count == 0)
            return null;

        return QueryResultFormattingService.BuildCsv(ResultColumns.ToList(), DisplayResultRows);
    }

    // ── Export Panel ──────────────────────────────────────────────────────────

    private readonly ResultChartViewModel _chart = new();
    public ResultChartViewModel Chart => _chart;

    [ObservableProperty]
    private bool _showChartPane;

    [ObservableProperty]
    private ExportOptions _exportOptions = new();

    [ObservableProperty]
    private bool _showExportOptionsPanel;

    /// <summary>Where exports are saved; the view supplies a save-dialog picker, tests a stand-in.</summary>
    public IExportPathPicker? ExportPathPicker { get; set; }

    public static IReadOnlyList<ExportFormat> ExportFormatOptions { get; } = Enum.GetValues<ExportFormat>();
    public static IReadOnlyList<CsvDelimiter> DelimiterOptions { get; } = Enum.GetValues<CsvDelimiter>();
    public static IReadOnlyList<NullRepresentation> NullRepOptions { get; } = Enum.GetValues<NullRepresentation>();

    [RelayCommand]
    private void ToggleExportOptionsPanel() => ShowExportOptionsPanel = !ShowExportOptionsPanel;

    [RelayCommand]
    private void ToggleChartPane() => ShowChartPane = !ShowChartPane;

    [RelayCommand]
    private void ToggleBlockComment() { /* handled in view */ }

    [RelayCommand]
    private void GoToLine(int lineNumber) { /* handled in view */ }

    [RelayCommand]
    private void DismissBanner() => ShowAnalyzeBanner = false;

    [RelayCommand]
    private void FormatSql()
    {
        QueryText = SqlFormatter.Format(QueryText, SelectedSession?.Capabilities.BatchDialect ?? SqlBatchDialect.Standard);
    }

    [RelayCommand]
    private void ToggleExplain() => ShowExplainPane = !ShowExplainPane;

    [RelayCommand]
    private async Task SaveCurrentQueryAsync()
    {
        if (_workspaceService == null)
            return;

        var name = string.IsNullOrWhiteSpace(SaveQueryName)
            ? $"Query {DateTime.Now:yyyy-MM-dd HHmmss}"
            : SaveQueryName.Trim();

        await _workspaceService.SaveQueryAsync(new SavedQueryDefinition
        {
            Name = name,
            Sql = QueryText,
            ProviderName = SelectedSession?.ProviderName,
            SavedAt = DateTimeOffset.UtcNow
        });

        SaveQueryName = name;
        await LoadSavedQueriesAsync();
    }

    private bool CanLoadSelectedHistory => SelectedHistoryEntry is { IsTruncated: false };

    [RelayCommand(CanExecute = nameof(CanLoadSelectedHistory))]
    private void LoadSelectedHistory()
    {
        if (CanLoadSelectedHistory)
            QueryText = SelectedHistoryEntry!.Sql;
    }

    [RelayCommand]
    private void OpenSelectedSavedQuery()
    {
        if (SelectedSavedQuery == null)
            return;

        QueryText = SelectedSavedQuery.Sql;
        if (!string.IsNullOrWhiteSpace(SelectedSavedQuery.Name))
            SaveQueryName = SelectedSavedQuery.Name;
    }

    [RelayCommand]
    private void InsertSelectedSnippet()
    {
        if (SelectedSnippet == null)
            return;

        InsertTextRequested?.Invoke(SelectedSnippet.Text);
    }

    [RelayCommand]
    private void ClearResultFilter()
    {
        ResultFilterText = string.Empty;
    }

    private async Task InitializeWorkspaceAsync()
    {
        if (_workspaceService == null)
            return;

        await LoadedSetting.ApplyAsync(() => TimeoutSeconds, _workspaceService.LoadTimeoutSecondsAsync, value => TimeoutSeconds = value);
        await LoadedSetting.ApplyAsync(() => ResultRowCap, _workspaceService.LoadResultRowCapAsync, value => ResultRowCap = value);
        await LoadSavedQueriesAsync();
        await LoadHistoryAsync();
    }

    private async Task LoadSavedQueriesAsync()
    {
        if (_workspaceService == null)
            return;

        var saved = await _workspaceService.LoadSavedQueriesAsync();
        SavedQueries.Clear();
        foreach (var query in saved)
            SavedQueries.Add(query);
    }

    private async Task LoadHistoryAsync()
    {
        if (_workspaceService == null)
            return;

        var history = await _workspaceService.LoadHistoryAsync(SelectedSession?.Connection);
        RecentQueries.Clear();
        foreach (var entry in history)
            RecentQueries.Add(entry);
    }

    private void LoadProviderSnippets()
    {
        AvailableSnippets.Clear();
        foreach (var snippet in SqlSnippetProvider.GetSnippets(SelectedSession?.ProviderName ?? CurrentIndex?.Provider))
            AvailableSnippets.Add(snippet);

        SelectedSnippet = AvailableSnippets.FirstOrDefault();
    }

    private void RebuildDisplayedResults(QueryResult? result)
    {
        ResultColumns.Clear();
        _allDisplayRows.Clear();
        _allDisplayRows.AddRange(result == null ? [] : QueryResultFormattingService.BuildDisplayRows(result));
        ResultEditor.Load(result, _allDisplayRows.ToList(), SelectedSession);
        foreach (var column in result?.HasResultSet == true ? result.ColumnInfos : [])
            ResultColumns.Add(column.Name);
        ResultGrid.Load(ResultColumns, SelectedSession?.Capabilities);

        if (result == null || !result.HasResultSet)
        {
            DisplayResultRows = [];
            return;
        }

        if (string.IsNullOrWhiteSpace(ResultSortColumn) && ResultColumns.Count > 0)
            ResultSortColumn = ResultColumns[0];

        ApplyDisplayResultView();
    }

    private void ApplyDisplayResultView()
    {
        IEnumerable<QueryResultDisplayRow> rows = _allDisplayRows;

        if (!string.IsNullOrWhiteSpace(ResultFilterText))
        {
            var filter = ResultFilterText.Trim();
            rows = rows.Where(row =>
                row.DisplayValues.Any(value =>
                    value.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(ResultSortColumn))
        {
            // Duplicate column names resolve to their first occurrence; a header click on the
            // second "id" in `SELECT a.id, b.id` is not distinguishable from the first by name alone.
            var sortOrdinal = ResultColumns.IndexOf(ResultSortColumn);
            if (sortOrdinal >= 0)
            {
                rows = ResultSortDescending
                    ? rows.OrderByDescending(row => GetSortValue(row, sortOrdinal))
                    : rows.OrderBy(row => GetSortValue(row, sortOrdinal));
            }
        }

        // Replace the collection once: 50,000 individual collection-change notifications
        // would make the DataGrid relayout on every added row.
        DisplayResultRows = new ObservableCollection<IReadOnlyList<string>>(
            rows.Select(row => row.DisplayValues));
    }

    private static IComparable? GetSortValue(QueryResultDisplayRow row, int ordinal)
    {
        var rawValue = ordinal < row.RawValues.Length ? row.RawValues[ordinal] : null;
        return rawValue switch
        {
            null => null,
            IComparable comparable => comparable,
            _ => QueryResultFormattingService.FormatValue(rawValue)
        };
    }
}
