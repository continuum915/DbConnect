using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// View model for a live editable Table Data tab.
/// Supports paging, inline editing, change tracking, commit preview, and row copy workflows.
/// </summary>
public partial class TableDataTabViewModel : ViewModelBase, IResultEditingHost
{
    private readonly DatabaseSession? _session;
    private readonly TableDataOperationCoordinator _operations = new();
    private readonly object _dbSessionLock = new();
    private IDbSession? _dbSession;
    private int _dbSessionGeneration;
    private bool _dbSessionClosed;
    private readonly SchemaNode? _tableNode;
    private readonly QueryWorkspaceService? _workspaceService;
    private ResultEditingViewModel? _editing;
    public ResultEditingViewModel Editing => _editing ??= CreateEditing();

    private ResultEditingViewModel CreateEditing()
    {
        var editing = new ResultEditingViewModel(this);
        editing.CommitPreviewRequested += request => CommitPreviewRequested?.Invoke(request);
        editing.ConcurrencyConflictRequested += request => ConcurrencyConflictRequested?.Invoke(request);
        editing.RevertStarting += () => RevertStarting?.Invoke();
        editing.RowValuesReverted += OnRowValuesReverted;
        return editing;
    }

    IReadOnlyList<string> IResultEditingHost.PrimaryKeyColumns => _primaryKeyColumns;
    bool IResultEditingHost.HasConnection => _session != null;
    Task<IDbSession> IResultEditingHost.OpenEditingSessionAsync() => GetOrOpenDbSessionAsync();
    Task IResultEditingHost.ReloadResultsAsync() => RefreshInternalAsync(bypassDirtyCheck: true);
    void IResultEditingHost.SetEditingStatus(string? message) => SetBaseStatus(message);
    void IResultEditingHost.RefreshEditingState() => RefreshDirtyState();
    private bool _workspaceInitialized;
    private string? _baseStatusMessage;
    private List<string> _primaryKeyColumns = [];
    private List<string> _stableOrderColumns = [];
    private Dictionary<string, ColumnDataType> _columnTypes = new(StringComparer.Ordinal);
    private Dictionary<string, EnumColumnInfo> _enumColumns = new(StringComparer.Ordinal);
    private string? _enumColumnsLoadedForTable;

    [ObservableProperty]
    private string _tableName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanDuplicateSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanAddRows))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isCommitting;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirtyRows))]
    [NotifyPropertyChangedFor(nameof(DirtySummary))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private ObservableCollection<EditableRow> _rows = new();

    [ObservableProperty]
    private ObservableCollection<string> _columns = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRow))]
    [NotifyPropertyChangedFor(nameof(CanDuplicateSelectedRow))]
    private EditableRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(CanMovePrevious))]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(CanMoveNext))]
    private bool _hasNextPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    private int _pageSize = 1000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    private long? _totalRowCount;

    /// <summary>When true, <see cref="RefreshInternalAsync"/> skips the exact COUNT(*) query and the total shows as unknown; useful for very large tables.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    private bool _skipExactCount;

    /// <summary>True when this provider can compute an exact total for the current filters.</summary>
    public bool SupportsExactCount => UsesServerSideSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityModeLabel))]
    [NotifyPropertyChangedFor(nameof(IdentityModeSummary))]
    [NotifyPropertyChangedFor(nameof(CanEditExistingRows))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRow))]
    private bool _allowAllColumnIdentityFallback;

    private readonly List<TableSortKey> _sortKeys = [];

    /// <summary>The active ORDER BY keys, primary key first.</summary>
    public IReadOnlyList<TableSortKey> SortKeys => _sortKeys;

    /// <summary>Text for the status bar, e.g. "Group ▲, Name ▼"; empty when unsorted.</summary>
    public string SortSummary => string.Join(", ", _sortKeys.Select(k => $"{k.Column} {(k.Descending ? "▼" : "▲")}"));

    /// <summary>Filter rows being edited in the filter bar; only <see cref="ApplyFiltersAsync"/> sends them to the server.</summary>
    public ObservableCollection<TableFilterRowViewModel> FilterRows { get; } = new();

    public IReadOnlyList<TableFilterOperator> FilterOperators { get; } = Enum.GetValues<TableFilterOperator>();

    private IReadOnlyList<TableFilter> _appliedFilters = [];
    private string? _appliedRawWhere;

    /// <summary>A raw WHERE fragment being edited; sent with the column filters by <see cref="ApplyFiltersAsync"/>.</summary>
    [ObservableProperty]
    private string _rawWhere = string.Empty;

    /// <summary>True when the provider can filter on the server, so the column filter bar is usable.</summary>
    public bool CanUseColumnFilters => UsesServerSideSort;

    public TableFilterRowViewModel AddFilterRow(
        string? column = null,
        TableFilterOperator filterOperator = TableFilterOperator.Equals,
        string? value = null)
    {
        var row = new TableFilterRowViewModel { Column = column, Operator = filterOperator, Value = value };
        FilterRows.Add(row);
        return row;
    }

    [RelayCommand]
    private void AddEmptyFilterRow() => AddFilterRow(Columns.FirstOrDefault());

    [RelayCommand]
    private void RemoveFilterRow(TableFilterRowViewModel? row)
    {
        if (row is not null)
            FilterRows.Remove(row);
    }

    /// <summary>The filter rows as spec predicates; rows with no column chosen are ignored.</summary>
    public IReadOnlyList<TableFilter> BuildFilters() =>
        FilterRows
            .Where(r => !string.IsNullOrEmpty(r.Column))
            .Select(r => r.NeedsValue
                ? new TableFilter(r.Column!, r.Operator, r.Value ?? string.Empty) { ParameterValue = r.ParameterValue }
                : new TableFilter(r.Column!, r.Operator))
            .ToList();

    [RelayCommand]
    public async Task ApplyFiltersAsync()
    {
        if (!CanReloadServerSide("filters"))
            return;

        _appliedFilters = BuildFilters();
        _appliedRawWhere = RawWhere;
        CurrentPageIndex = 0;
        await RefreshInternalAsync(bypassDirtyCheck: true);
    }

    [RelayCommand]
    public async Task ClearFiltersAsync()
    {
        if (!CanReloadServerSide("filters"))
            return;

        FilterRows.Clear();
        RawWhere = string.Empty;
        _appliedFilters = [];
        _appliedRawWhere = null;
        CurrentPageIndex = 0;
        await RefreshInternalAsync(bypassDirtyCheck: true);
    }

    /// <summary>Sets a single equals filter and reloads; used to jump to a specific row from elsewhere in the app (e.g. Find in Data, FK navigation).</summary>
    public Task ApplyColumnFilterAsync(string column, string value)
    {
        FilterRows.Clear();
        AddFilterRow(column, TableFilterOperator.Equals, value);
        RawWhere = string.Empty;
        return ApplyFiltersAsync();
    }

    /// <summary>Sets a single equals filter before the tab's first load, so the initial page already reflects it instead of loading unfiltered then reloading.</summary>
    public void PresetFilter(string column, TableFilterOperator op, string value)
    {
        FilterRows.Clear();
        AddFilterRow(column, op, value);
        _appliedFilters = BuildFilters();
    }

    private string? _relationshipTableName;

    public IReadOnlyList<ForeignKeyRelationship> ForeignKeys { get; private set; } = [];

    public async Task LoadForeignKeysAsync()
    {
        if (!CanUseColumnFilters || _session?.Capabilities.SupportsForeignKeys != true) return;
        var session = await GetOrOpenDbSessionAsync();
        _relationshipTableName = await ForeignKeyService.ResolveTableNameAsync(session, TableObjectName);
        ForeignKeys = await ForeignKeyService.LoadAsync(session);
    }

    public bool MatchesTable(string table) => string.Equals(table,
        _relationshipTableName ?? TableObjectName, StringComparison.Ordinal);

    public void PresetFilters(IReadOnlyList<TableFilter> filters)
    {
        FilterRows.Clear();
        RawWhere = string.Empty;
        _appliedRawWhere = null;
        foreach (var filter in filters)
            AddFilterRow(filter.Column, filter.Operator, filter.Value).ParameterValue = filter.ParameterValue;
        _appliedFilters = BuildFilters();
    }

    public TableDataTabViewModel? CreateRelatedTab(EditableRow row, ForeignKeyRelationship relationship, bool referencing)
    {
        if (_session == null || !CanUseColumnFilters) return null;
        var source = referencing ? relationship.ReferencedColumns : relationship.Columns;
        var target = referencing ? relationship.Columns : relationship.ReferencedColumns;
        var filters = new List<TableFilter>();
        for (var i = 0; i < source.Count; i++)
        {
            if (!row.Values.TryGetValue(source[i], out var value) || value is null or DBNull) return null;
            filters.Add(new(target[i], TableFilterOperator.Equals, Convert.ToString(value, CultureInfo.InvariantCulture))
            { ParameterValue = value });
        }
        var tab = new TableDataTabViewModel(referencing ? relationship.Table : relationship.ReferencedTable,
            _session, _workspaceService);
        tab.PresetFilters(filters);
        return tab;
    }

    public async Task<IReadOnlyList<ForeignKeyLookupValue>> LoadForeignKeyValuesAsync(ForeignKeyRelationship relationship, int pageIndex = 0)
    {
        var session = await GetOrOpenDbSessionAsync();
        var page = await session.LoadTablePageAsync(relationship.ReferencedTable,
            new TableQuerySpec(relationship.ReferencedColumns.Select(c => new TableSortKey(c)).ToList(), pageIndex, 100));
        if (page.Result.HasError) throw new InvalidOperationException(page.Result.ErrorMessage);
        return page.Result.ObjectRows.Select(row => new ForeignKeyLookupValue(
            relationship.ReferencedColumns.Select(c => page.Result.ValueOrDefault(row, c)).ToList())).ToList();
    }

    public void ApplyForeignKeyValue(EditableRow row, ForeignKeyRelationship relationship, ForeignKeyLookupValue value)
    {
        if (IsCommitting || !Rows.Contains(row) || row.State == EditState.Deleted
            || (row.State != EditState.Inserted && !CanEditExistingRows)) return;
        if (value.Values.Count != relationship.Columns.Count) throw new ArgumentException("Incomplete foreign key value.", nameof(value));
        for (var i = 0; i < relationship.Columns.Count; i++)
        {
            row.Values[relationship.Columns[i]] = value.Values[i];
            MarkRowModified(row, relationship.Columns[i]);
        }
        OnRowValuesReverted([row]);
    }

    // RAIN-P2-14 — Column layout (hide/show, order, frozen leading columns), remembered per
    // connection and table through the workspace service.
    private TableColumnLayout _columnLayout = new();
    private string? _columnLayoutTableName;

    /// <summary>Raised after the column layout changes, so the view can rebuild its grid columns.</summary>
    public event Action? ColumnLayoutChanged;

    /// <summary>Every column, visible or not, in layout order.</summary>
    public IReadOnlyList<string> LayoutColumns => _columnLayout.Arrange(Columns);

    /// <summary>The visible columns, in layout order.</summary>
    public IReadOnlyList<string> DisplayedColumns => _columnLayout.VisibleAmong(Columns);

    /// <summary>The hidden columns, in layout order.</summary>
    public IReadOnlyList<string> HiddenColumns => _columnLayout.HiddenAmong(Columns);

    /// <summary>How many leading <see cref="DisplayedColumns"/> stay in view while scrolling horizontally.</summary>
    public int FrozenColumnCount => _columnLayout.FrozenAmong(Columns);

    public Task HideColumnAsync(string column) => SaveColumnLayoutIfAsync(_columnLayout.Hide(Columns, column));

    public Task ShowColumnAsync(string column) => SaveColumnLayoutIfAsync(_columnLayout.Show(Columns, column));

    public Task ShowAllColumnsAsync() => SaveColumnLayoutIfAsync(_columnLayout.ShowAll(Columns));

    /// <summary>Moves a visible column to <paramref name="displayIndex"/> among <see cref="DisplayedColumns"/>.</summary>
    public Task MoveColumnAsync(string column, int displayIndex) =>
        SaveColumnLayoutIfAsync(_columnLayout.Move(Columns, column, displayIndex));

    /// <summary>Freezes the visible columns up to and including <paramref name="column"/>.</summary>
    public Task FreezeColumnsThroughAsync(string column) =>
        SaveColumnLayoutIfAsync(_columnLayout.FreezeThrough(Columns, column));

    public Task UnfreezeColumnsAsync() => SaveColumnLayoutIfAsync(_columnLayout.Unfreeze());

    private async Task SaveColumnLayoutIfAsync(bool changed)
    {
        if (!changed)
            return;

        ColumnLayoutChanged?.Invoke();
        if (_workspaceService != null && _session != null)
            await _workspaceService.SaveTableColumnLayoutAsync(
                _session.Connection, _columnLayoutTableName ?? TableObjectName, _columnLayout);
    }

    // A tab restored from the last session is opened by its bare name while one opened from the
    // explorer or by FK navigation is schema-qualified; resolving the name the same way FK
    // navigation does keys all three to one saved layout.
    private async Task LoadColumnLayoutAsync(QueryWorkspaceService workspaceService, DatabaseSession session)
    {
        try
        {
            _columnLayoutTableName = await ForeignKeyService.ResolveTableNameAsync(await GetOrOpenDbSessionAsync(), TableObjectName);
        }
        catch (InvalidOperationException)
        {
            _columnLayoutTableName = TableObjectName;
        }

        _columnLayout = await workspaceService.LoadTableColumnLayoutAsync(session.Connection, _columnLayoutTableName);
    }

    private bool CanReloadServerSide(string what)
    {
        if (!UsesServerSideSort)
        {
            SetBaseStatus($"⚠️ This provider does not support server-side {what} yet.");
            return false;
        }

        if (HasDirtyRows)
        {
            SetBaseStatus($"⚠️ You have unsaved changes. Commit or Revert before changing the {what}.");
            return false;
        }

        return true;
    }

    private bool UsesServerSideSort => _session?.Capabilities.SupportsTableQuerySpec == true;

    /// <summary>
    /// Reacts to the exact-total opt-out changing. Bound directly (two-way) to the status bar checkbox,
    /// so the checkbox and this property can never disagree: turning it off just hides the total, and
    /// turning it back on re-fetches it, reverting (and re-syncing the checkbox) if that isn't possible
    /// right now — e.g. unsaved changes are pending.
    /// </summary>
    partial void OnSkipExactCountChanged(bool value)
    {
        if (value)
        {
            TotalRowCount = null;
            return;
        }

        if (!CanReloadServerSide("row count"))
        {
            SkipExactCount = true;
            return;
        }

        _ = RefreshInternalAsync(bypassDirtyCheck: true);
    }

    /// <summary>
    /// Cycles a column through ascending, descending, and unsorted. A plain click makes it the only
    /// sort key; an additive click keeps the other keys and appends or cycles this one.
    /// </summary>
    public async Task SortByColumn(string? columnName, bool additive = false)
    {
        if (columnName is null)
            return;

        if (HasDirtyRows)
        {
            SetBaseStatus("⚠️ You have unsaved changes. Commit or Revert before changing the sort.");
            return;
        }

        var index = _sortKeys.FindIndex(k => k.Column == columnName);
        var next = index < 0 ? new TableSortKey(columnName)
            : !_sortKeys[index].Descending ? _sortKeys[index] with { Descending = true }
            : null;

        if (!additive)
            _sortKeys.Clear();
        else if (index >= 0)
            _sortKeys.RemoveAt(index);

        if (next is not null)
        {
            if (additive && index >= 0)
                _sortKeys.Insert(index, next);
            else
                _sortKeys.Add(next);
        }

        OnPropertyChanged(nameof(SortSummary));

        CurrentPageIndex = 0;
        await RefreshInternalAsync(bypassDirtyCheck: true);
    }

    /// <summary>
    /// RAIN-P2-10 — Builds a standalone INSERT for the "Copy as Insert" context-menu action, sharing
    /// <see cref="DataChangeTracker"/>'s provider-quoted, type-correct SQL rendering instead of the
    /// hand-rolled quoting the ad-hoc builder used to do here.
    /// </summary>
    public string BuildInsertSql(EditableRow row)
    {
        var tracker = new DataChangeTracker(TableObjectName, Columns.ToList(), _primaryKeyColumns, _session?.Capabilities) { Object = ObjectSelection.Identity };
        var syntheticRow = new EditableRow(new Dictionary<string, object?>(row.Values), editedColumns: Columns)
        {
            State = EditState.Inserted
        };
        return tracker.BuildStatement(syntheticRow);
    }

    public bool HasDirtyRows => Rows.Any(r => r.IsDirty);
    public override bool IsDirty => HasDirtyRows;
    public override FluentIcons.Common.Symbol? TabIcon => FluentIcons.Common.Symbol.Table;
    public int DirtyRowCount => Rows.Count(r => r.IsDirty);
    public bool HasSelection => SelectedRow != null;
    public bool HasReliableIdentity => _primaryKeyColumns.Count > 0;
    public bool CanEditExistingRows => !_metadataRequiresReload && (HasReliableIdentity || AllowAllColumnIdentityFallback);
    public bool CanAddRows => !_metadataRequiresReload && !IsCommitting;
    public bool CanCommit => !_metadataRequiresReload && HasDirtyRows && !IsCommitting;
    public bool CanRevert => HasDirtyRows && !IsCommitting;
    public bool CanRefresh => !IsLoading && !IsCommitting && !HasDirtyRows;
    public bool CanMovePrevious => CanRefresh && CurrentPageIndex > 0;
    public bool CanMoveNext => CanRefresh && HasNextPage;
    public bool CanDeleteSelectedRow => !IsCommitting && SelectedRow != null && (SelectedRow.State == EditState.Inserted || CanEditExistingRows);
    public bool CanDuplicateSelectedRow => !IsCommitting && SelectedRow != null;
    public DatabaseSession? Session => _session;
    public TableObjectSelection ObjectSelection { get; } = new(null, null);

    /// <summary>True while this tab holds an open database session.</summary>
    public bool HasDbSession => _dbSession != null;
    public string TableObjectName => ObjectSelection.CompatibilityName(_tableNode, TableName);
    public string CommitModeLabel => _session?.Capabilities.TransactionStatusLabel ?? "Commit behavior unavailable";
    public string CommitModeSummary => _session?.Capabilities.TransactionCommitSummary ?? "Commit behavior depends on the active provider.";
    public string IdentityModeLabel => HasReliableIdentity
        ? $"Primary key identity ({string.Join(", ", _primaryKeyColumns)})"
        : AllowAllColumnIdentityFallback
            ? "All-column identity fallback enabled"
            : "Insert-only mode (no primary key)";
    public string IdentityModeSummary => HasReliableIdentity
        ? "Existing rows can be updated and deleted safely because the table exposes a primary key."
        : AllowAllColumnIdentityFallback
            ? "Existing-row update/delete uses all original column values in the WHERE clause for this tab."
            : "Existing-row update/delete is blocked until you explicitly enable all-column identity fallback for this tab.";
    public string PageSummary
    {
        get
        {
            if (TotalRowCount is long total)
            {
                var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(PageSize, 1)));
                return $"Page {CurrentPageIndex + 1} of {totalPages:N0} · {total:N0} row(s) total · Page size {PageSize:N0}";
            }

            var nextIndicator = HasNextPage ? " · more rows available" : string.Empty;
            return $"Page {CurrentPageIndex + 1} · {Rows.Count:N0} row(s) loaded · Page size {PageSize:N0}{nextIndicator}";
        }
    }

    public string DirtySummary
    {
        get
        {
            if (!HasDirtyRows)
                return "No unsaved changes";

            var parts = new List<string>();
            var inserted = Rows.Count(r => r.State == EditState.Inserted);
            var modified = Rows.Count(r => r.State == EditState.Modified);
            var deleted = Rows.Count(r => r.State == EditState.Deleted);
            if (inserted > 0)
                parts.Add($"{inserted} new");
            if (modified > 0)
                parts.Add($"{modified} edited");
            if (deleted > 0)
                parts.Add($"{deleted} deleted");
            return $"Unsaved changes: {string.Join(", ", parts)}";
        }
    }

    public IReadOnlyList<int> PageSizeChoices { get; } = [100, 250, 500, 1000, 5000];

    public TableDataTabViewModel(string tableName, DatabaseSession session, QueryWorkspaceService? workspaceService = null, SchemaNode? tableNode = null)
    {
        _session = session;
        _workspaceService = workspaceService;
        _tableNode = tableNode;
        ObjectSelection = new(session, tableNode);
        TableName = tableName;
        Title = tableName;
        Editing.ObserveRows(Rows);
        InitializeMetadata();
        UpdateTitle();
        UpdateStatusMessage();
    }

    public TableDataTabViewModel(string tableName, DatabaseSession session, SchemaNode? tableNode)
        : this(tableName, session, null, tableNode)
    {
    }

    public TableDataTabViewModel(string tableName, IDatabaseProvider provider, string connectionString, SchemaNode? tableNode = null)
        : this(
            tableName,
            new DatabaseSession(
                new ConnectionInfo
                {
                    Name = tableName,
                    Provider = provider.ProviderName,
                    ConnectionString = connectionString
                },
                provider),
            null,
            tableNode)
    {
    }

    public TableDataTabViewModel()
    {
        Editing.ObserveRows(Rows);
        UpdateTitle();
        UpdateStatusMessage();
    }

    partial void OnRowsChanged(ObservableCollection<EditableRow> value)
    {
        Editing.ObserveRows(value);
        RefreshDirtyState();
    }

    partial void OnPageSizeChanged(int value)
    {
        if (value <= 0)
        {
            PageSize = 1000;
            return;
        }

        if (_workspaceService != null)
            _ = _workspaceService.SaveTablePageSizeAsync(value);

        if (_workspaceInitialized && Rows.Count > 0 && !IsLoading && !HasDirtyRows)
        {
            CurrentPageIndex = 0;
            _ = RefreshInternalAsync(bypassDirtyCheck: true);
        }
    }

    partial void OnAllowAllColumnIdentityFallbackChanged(bool value)
    {
        UpdateStatusMessage();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        UpdateStatusMessage();
    }

    /// <summary>
    /// The tab's own database session, opened on first use. Tabs never share one,
    /// even when they target the same connection.
    /// </summary>
    public async Task<IDbSession> GetOrOpenDbSessionAsync(CancellationToken cancellationToken = default)
    {
        var connection = _session ?? throw new InvalidOperationException("No active connection selected.");

        // One open at a time, so overlapping first uses share a single session.
        return await _operations.OpenSessionAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_dbSessionClosed, this);
            if (_dbSession != null)
                return _dbSession;

            int generation;
            lock (_dbSessionLock)
                generation = _dbSessionGeneration;

            var opened = new ExportDbSession(await SessionSetup.OpenAsync(connection, cancellationToken));

            // A release since the open began makes this session stale.
            lock (_dbSessionLock)
            {
                if (generation == _dbSessionGeneration)
                {
                    _dbSession = opened;
                    return opened;
                }
            }

            await opened.DisposeAsync();
            throw new ObjectDisposedException(nameof(TableDataTabViewModel));
        }, cancellationToken);
    }

    /// <summary>Closes the tab for good: disposes its session, and any read or commit still in flight ends without opening another. Safe to call repeatedly.</summary>
    public async Task ReleaseDbSessionAsync()
    {
        _metadataSubscription?.Dispose();
        IDbSession? session;
        lock (_dbSessionLock)
        {
            _dbSessionClosed = true;
            _dbSessionGeneration++;
            session = _dbSession;
            _dbSession = null;
        }

        if (session == null)
            return;

        try
        {
            await session.DisposeAsync();
        }
        catch
        {
            // Releasing a session must never block closing a tab.
        }
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshInternalAsync(bypassDirtyCheck: false);

    private TableQuerySpec BuildQuerySpec(int pageIndex)
    {
        // Key columns follow the user's sort so ties page deterministically.
        var sort = _sortKeys
            .Concat(_stableOrderColumns
                .Where(c => !_sortKeys.Any(k => k.Column == c))
                .Select(c => new TableSortKey(c)))
            .ToList();
        return new TableQuerySpec(sort, pageIndex, PageSize) { Object = ObjectSelection.Identity, Filters = _appliedFilters, RawWhere = _appliedRawWhere };
    }

    private Task<TablePageResult> LoadPageAsync(IDbSession dbSession, int pageIndex) =>
        dbSession.LoadTablePageAsync(TableObjectName, BuildQuerySpec(pageIndex));

    public Task RefreshInternalAsync(bool bypassDirtyCheck) => _operations.RefreshAsync(async () =>
    {
        if (_session == null)
            return;
        if (!await ObjectSelection.ResolveAsync(_session))
        {
            SetBaseStatus("Select this table again in the explorer: its saved name is ambiguous or unavailable.");
            return;
        }
        await EnsureWorkspaceLoadedAsync();

        if (!bypassDirtyCheck && HasDirtyRows)
        {
            SetBaseStatus("⚠️ You have unsaved changes. Commit or Revert before changing pages or refreshing.");
            return;
        }

        IsLoading = true;
        SetBaseStatus(null);
        var selectionIdentity = Editing.BuildIdentitySnapshot(SelectedRow);
        try
        {
            var dbSession = await GetOrOpenDbSessionAsync();
            (_primaryKeyColumns, _stableOrderColumns) = await ObjectSelection.LoadOrderColumnsAsync(_session, _primaryKeyColumns, _stableOrderColumns);
            var metadataScope = new MetadataScope(ObjectSelection.Identity, SchemaNodeType.Column);
            var metadataRevision = ObjectSelection.MetadataRevision;
            bool MetadataIsCurrent() => metadataRevision == _session.Catalog.GetSnapshot(metadataScope).Revision;
            var page = await LoadPageAsync(dbSession, CurrentPageIndex);

            if (!page.Result.HasError && CurrentPageIndex > 0 && page.Result.ObjectRows.Count == 0)
            {
                CurrentPageIndex--;
                page = await LoadPageAsync(dbSession, CurrentPageIndex);
            }

            if (!MetadataIsCurrent()) return;
            Rows.Clear();

            if (page.Result.HasError)
            {
                // Columns stay so the filter bar can still offer them while the user fixes the filter.
                SetBaseStatus(ConnectionLoss.Describe(dbSession, _session.Name, $"❌ {page.Result.ErrorMessage}"));
                HasNextPage = false;
                TotalRowCount = null;
                return;
            }

            Columns.Clear();
            _loadedExportColumns = page.Result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal, c.ProviderTypeName, c.ClrType)).ToArray();
            _columnTypes = new Dictionary<string, ColumnDataType>(StringComparer.Ordinal);

            foreach (var column in page.Result.ColumnInfos)
            {
                Columns.Add(column.Name);
                _columnTypes[column.Name] = ColumnTypeResolver.Resolve(column);
            }

            await EnsureEnumColumnsLoadedAsync();

            foreach (var rawRow in page.Result.ObjectRows)
                Rows.Add(new EditableRow(page.Result.ToRowDictionary(rawRow)));

            HasNextPage = page.HasNextPage;

            TotalRowCount = UsesServerSideSort && !SkipExactCount
                ? await dbSession.CountTableRowsAsync(TableObjectName, BuildQuerySpec(CurrentPageIndex))
                : null;

            if (!MetadataIsCurrent()) return;
            SelectedRow = selectionIdentity != null
                ? Editing.FindMatchingRow(selectionIdentity)
                : Rows.FirstOrDefault();

            _metadataRequiresReload = false;
            Editing.Tracker = new DataChangeTracker(
                TableObjectName,
                Columns.ToList(),
                _primaryKeyColumns,
                _session.Capabilities,
                _session.Capabilities.NonComparableColumns(page.Result.ColumnInfos))
            { Object = ObjectSelection.Identity };

            SetBaseStatus($"{Rows.Count:N0} row(s) loaded.");
        }
        catch (ObjectDisposedException)
        {
            // The tab was closed while loading.
        }
        finally
        {
            IsLoading = false;
            RefreshDirtyState();
        }
    });

    /// <summary>The classified type for a column, used to pick a culture-invariant cell converter/editor.</summary>
    public ColumnDataType GetColumnType(string columnName) =>
        _columnTypes.GetValueOrDefault(columnName, ColumnDataType.Unknown);

    /// <summary>RAIN-P2-09 — The allowed values for an Enum/Set column, or empty when the provider
    /// reported none (in which case <see cref="GetEditorKind"/> falls back to a plain text editor).</summary>
    public IReadOnlyList<string> GetEnumValues(string columnName) =>
        _enumColumns.TryGetValue(columnName, out var info) ? info.Values : [];

    /// <summary>RAIN-P2-08/09 — Which editor control the grid should show for this column.</summary>
    public CellEditorKind GetEditorKind(string columnName) => GetColumnType(columnName) switch
    {
        ColumnDataType.Boolean => CellEditorKind.Boolean,
        ColumnDataType.Integer or ColumnDataType.Real or ColumnDataType.Decimal => CellEditorKind.Numeric,
        ColumnDataType.Date => CellEditorKind.Date,
        ColumnDataType.DateTime => CellEditorKind.DateTime,
        ColumnDataType.Enum when GetEnumValues(columnName).Count > 0 => CellEditorKind.Enum,
        ColumnDataType.Set when GetEnumValues(columnName).Count > 0 => CellEditorKind.Set,
        _ => CellEditorKind.Text
    };

    /// <summary>
    /// RAIN-P2-08 — Flips a cell between NULL and a type-correct non-NULL placeholder
    /// (<see cref="CellValueConverter.DefaultNonNullValue"/>), so the "NULL" toggle never writes
    /// a bare empty string into a numeric/date column. Boolean columns don't route through here —
    /// their tri-state checkbox represents NULL directly.
    /// </summary>
    public void ToggleCellNull(EditableRow row, string columnName)
    {
        row.Values[columnName] = row.Values.GetValueOrDefault(columnName) == null
            ? DefaultNonNullValue(columnName)
            : null;
        MarkRowModified(row, columnName);
    }

    /// <summary>
    /// RAIN-P2-09 — An ENUM column has no generic type-correct placeholder the way other types do:
    /// <see cref="CellValueConverter.DefaultNonNullValue"/> falls back to "", but "" is not a member
    /// of the column's defined value set (MySQL strict mode rejects it outright). The first value the
    /// provider reported is the only string guaranteed to be valid. A MySQL SET column has no such
    /// problem — "" legitimately means "no members selected" — so it still uses the generic default.
    /// </summary>
    private object DefaultNonNullValue(string columnName)
    {
        var type = GetColumnType(columnName);
        if (type == ColumnDataType.Enum && GetEnumValues(columnName) is { Count: > 0 } values)
            return values[0];

        return CellValueConverter.DefaultNonNullValue(type);
    }

    /// <summary>
    /// RAIN-P2-10 — <paramref name="columnName"/> records which cell was just edited, so an
    /// <see cref="EditState.Inserted"/> row's <see cref="EditableRow.EditedColumns"/> only grows for
    /// columns the user actually touched; untouched identity/generated/default columns stay out of
    /// the eventual INSERT.
    /// </summary>
    public void MarkRowModified(EditableRow row, string? columnName = null) => Editing.MarkRowModified(row, columnName);

    [RelayCommand]
    private void AddRow() => Editing.AddRow();

    [RelayCommand]
    private void DeleteRow(EditableRow? row) => Editing.DeleteRow(row);

    [RelayCommand]
    private void DuplicateSelectedRow() => Editing.DuplicateSelectedRow();

    [RelayCommand]
    private Task CommitAsync() => Editing.CommitAsync();

    public Task ExecuteCommitAsync() => Editing.ExecuteCommitAsync();

    public Task ReloadConflictedRowAsync(EditableRow row) => Editing.ReloadConflictedRowAsync(row);

    public Task OverwriteConflictedRowAsync(EditableRow row) => Editing.OverwriteConflictedRowAsync(row);

    [RelayCommand]
    private void Revert() => Editing.Revert();

    public bool CanRevertCell([NotNullWhen(true)] EditableRow? row, [NotNullWhen(true)] string? column) => Editing.CanRevertCell(row, column);

    public void RevertCell(EditableRow row, string column) => Editing.RevertCell(row, column);

    public bool CanRevertRow([NotNullWhen(true)] EditableRow? row) => Editing.CanRevertRow(row);

    [RelayCommand]
    private void RevertRow(EditableRow? row) => Editing.RevertRow(row);

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanMoveNext)
            return;

        CurrentPageIndex++;
        await RefreshInternalAsync(bypassDirtyCheck: true);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!CanMovePrevious)
            return;

        CurrentPageIndex--;
        await RefreshInternalAsync(bypassDirtyCheck: true);
    }

    [RelayCommand]
    private async Task ReloadCurrentPageAsync()
    {
        await RefreshInternalAsync(bypassDirtyCheck: false);
    }

    public string? BuildCopyRowsText(IReadOnlyList<EditableRow> rows)
    {
        if (rows.Count == 0 || Columns.Count == 0)
            return null;

        var displayRows = rows.Select(row =>
            Columns.Select(column => QueryResultFormattingService.FormatValue(row.Values.GetValueOrDefault(column))).ToList());

        return QueryResultFormattingService.BuildTabSeparated(Columns.ToList(), displayRows);
    }

    public int PasteRowsFromText(string text) => Editing.PasteRowsFromText(text);

    public event Action<CommitPreviewRequest>? CommitPreviewRequested;

    /// <summary>RAIN-P2-12 — Raised when a commit found a row changed underneath the user; the view offers reload or overwrite.</summary>
    public event Action<ConcurrencyConflictRequest>? ConcurrencyConflictRequested;

    /// <summary>
    /// revert-grid-fixups — Raised with the rows still in the grid whose values a revert just restored.
    /// Table cells aren't bound to <see cref="EditableRow.Values"/>, so the view has to redraw them.
    /// </summary>
    public event Action<IReadOnlyList<EditableRow>>? RowValuesReverted;

    /// <summary>
    /// revert-grid-fixups — Raised just before a user-initiated revert changes any values, so the view
    /// can cancel an open cell editor first. Cancelling afterwards would let that editor's value land
    /// on top of the reverted row.
    /// </summary>
    public event Action? RevertStarting;

    private void OnRowValuesReverted(IReadOnlyList<EditableRow> rows)
    {
        RefreshDirtyState();
        RowValuesReverted?.Invoke(rows);
    }

    private void RefreshDirtyState()
    {
        OnPropertyChanged(nameof(HasDirtyRows));
        OnPropertyChanged(nameof(DirtyRowCount));
        OnPropertyChanged(nameof(DirtySummary));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        OnPropertyChanged(nameof(CanDuplicateSelectedRow));
        UpdateTitle();
        UpdateStatusMessage();
    }

    private void UpdateTitle()
    {
        var dirtySuffix = HasDirtyRows ? " *" : string.Empty;
        Title = $"{TableName}{dirtySuffix}";
    }

    private void SetBaseStatus(string? message)
    {
        _baseStatusMessage = message;
        UpdateStatusMessage();
    }

    private void UpdateStatusMessage()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_baseStatusMessage))
            parts.Add(_baseStatusMessage!);

        parts.Add(PageSummary);
        parts.Add(IdentityModeLabel);

        if (HasDirtyRows)
            parts.Add(DirtySummary);

        StatusMessage = string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

}
