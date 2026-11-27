using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// RAIN-P2-16 — Edits a query tab's current result in place when <see cref="QueryResultEditability"/>
/// finds it maps to one keyed table. Rows are <see cref="EditableRow"/>s keyed by source column, so
/// edits go through the same <see cref="DataChangeTracker"/>, commit preview and
/// <see cref="IDbSession.ExecuteTransactionAsync"/> path as the table data tab. A read-only result
/// carries <see cref="ReadOnlyReason"/> for the grid's tooltip.
/// </summary>
public partial class QueryResultEditorViewModel : ObservableObject
{
    private readonly Func<Task<IDbSession>> _openSession;
    private readonly Action? _afterResultChanged;
    // Rows are keyed by the result's own row arrays, which every rebuild of the display rows shares,
    // so pending edits survive the tab redrawing the same result.
    private readonly Dictionary<object?[], EditableRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<string>, object?[]> _rawRowsByDisplay = new(ReferenceEqualityComparer.Instance);
    private QueryResult? _result;
    // The connection each result was first loaded under, i.e. the one its query ran on.
    private readonly ConditionalWeakTable<QueryResult, DatabaseSession> _resultConnections = new();
    private QueryResultEditTarget? _target;
    private DataChangeTracker? _tracker;
    private ColumnDataType[] _columnTypes = [];
    private string _commitSummary = string.Empty;
    private int _loadGeneration;

    /// <param name="openSession">The tab's own session, which the commit runs on.</param>
    /// <param name="afterResultChanged">Runs after each commit attempt or conflict reload (e.g. to re-read
    /// the tab's transaction state and redraw the result).</param>
    public QueryResultEditorViewModel(Func<Task<IDbSession>> openSession, Action? afterResultChanged = null)
    {
        _openSession = openSession;
        _afterResultChanged = afterResultChanged;
    }

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Why the current result can't be edited; null when it can, or when there is no result set.</summary>
    [ObservableProperty]
    private string? _readOnlyReason;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardChangesCommand))]
    private bool _hasChanges;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCommitCommand))]
    private bool _isCommitting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The result's table has no usable primary key, but rows could be matched by all their values.</summary>
    [ObservableProperty]
    private bool _offersAllColumnIdentity;

    /// <summary>
    /// The user's opt-in to identify rows by all their original values, as the table tab offers. Each UPDATE or
    /// DELETE must then affect exactly one row, so identical rows make the commit fail and roll back.
    /// </summary>
    [ObservableProperty]
    private bool _allowAllColumnIdentity;

    private IReadOnlyList<QueryResultDisplayRow> _lastDisplayRows = [];
    private DatabaseSession? _lastConnection;

    partial void OnAllowAllColumnIdentityChanged(bool value)
    {
        var result = _result;
        if (result == null || HasChanges) return;
        Reset();
        Load(result, _lastDisplayRows, _lastConnection);
    }

    public event Action<CommitPreviewRequest>? CommitPreviewRequested;

    /// <summary>RAIN-P2-12's conflict dialog: a commit found a row changed or deleted since the query ran.</summary>
    public event Action<ConcurrencyConflictRequest>? ConcurrencyConflictRequested;

    /// <summary>Asks whether to discard the given number of edited rows; set by the view.</summary>
    public Func<int, Task<bool>>? DiscardConfirmation { get; set; }

    /// <summary>Raised when rows were replaced or reverted outside a cell edit, so the grid redraws them.</summary>
    public event Action? RowsRefreshed;

    /// <summary>The most recent <see cref="Load"/>, for callers that need to wait for it.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Starts <see cref="LoadAsync"/> without waiting; <see cref="PendingLoad"/> tracks it.</summary>
    public void Load(QueryResult? result, IReadOnlyList<QueryResultDisplayRow> displayRows, DatabaseSession? connection) =>
        PendingLoad = LoadAsync(result, displayRows, connection);

    /// <summary>
    /// Evaluates <paramref name="result"/> and, when it is editable, wraps each row. The grid shows
    /// <paramref name="displayRows"/>; <see cref="RowFor"/> maps each one's display values to its row.
    /// A load superseded by a newer one before its key lookup finishes is dropped. Reloading the
    /// result already loaded only remaps its display rows, keeping pending edits.
    /// </summary>
    public async Task LoadAsync(QueryResult? result, IReadOnlyList<QueryResultDisplayRow> displayRows, DatabaseSession? connection)
    {
        var generation = ++_loadGeneration;
        _rawRowsByDisplay.Clear();
        if (result != null && connection != null && !ReferenceEquals(_resultConnections.GetValue(result, _ => connection), connection))
        {
            // The tab switched connections since the query ran; a commit would go to the new one.
            Reset();
            ReadOnlyReason = "Read-only: this result came from another connection. Run the query again to edit it.";
            return;
        }

        _lastDisplayRows = displayRows;
        _lastConnection = connection;
        if (IsEditable && ReferenceEquals(result, _result))
        {
            // Rows fetched past the row cap since the last load get their own editable rows.
            foreach (var rawRow in result!.ObjectRows)
                if (!_rows.ContainsKey(rawRow)) _rows[rawRow] = NewRow(rawRow);
            MapDisplayRows(displayRows);
            return;
        }

        Reset();
        _result = result;

        if (result is not { HasResultSet: true, HasError: false } || connection == null)
            return;

        QueryResultEditability editability;
        try
        {
            editability = await QueryResultEditability.EvaluateAsync(result, connection.Capabilities,
                (schema, table) => LoadKeyColumnsAsync(connection, schema, table), AllowAllColumnIdentity);
        }
        catch (Exception ex)
        {
            editability = new QueryResultEditability(null, $"Read-only: the result's table key couldn't be read ({ex.Message}).");
        }

        if (generation != _loadGeneration)
            return;

        OffersAllColumnIdentity = editability.OffersAllColumnIdentity || (AllowAllColumnIdentity && editability.Target != null);
        if (editability.Target is not { } target)
        {
            ReadOnlyReason = editability.ReadOnlyReason;
            return;
        }

        _target = target;
        _columnTypes = result.ColumnInfos.Select(ColumnTypeResolver.Resolve).ToArray();
        _commitSummary = connection.Capabilities.TransactionCommitSummary;
        _tracker = new DataChangeTracker(
            target.Table,
            target.SourceColumns,
            target.KeyColumns,
            connection.Capabilities,
            result.ColumnInfos
                .Where(c => !connection.Capabilities.CanCompareType(c.ProviderTypeName))
                .Select(c => target.SourceColumns[c.Ordinal]),
            target.Schema);

        foreach (var rawRow in result.ObjectRows)
            _rows[rawRow] = NewRow(rawRow);
        MapDisplayRows(displayRows);

        IsEditable = true;
    }

    /// <summary>
    /// The table's primary key columns. A table the driver names without a schema (SQL Server, for
    /// an unqualified query) is looked up in the connection's default schema, where the
    /// unqualified UPDATE will also resolve it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> LoadKeyColumnsAsync(DatabaseSession connection, string? schema, string table)
    {
        var name = schema is not null
            ? $"{schema}.{table}"
            : await ForeignKeyService.ResolveTableNameAsync(new StatelessDbSession(connection.Provider, connection.Connection), table);
        return (await connection.GetColumnDefinitionsAsync(name)).Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
    }

    private void MapDisplayRows(IReadOnlyList<QueryResultDisplayRow> displayRows)
    {
        foreach (var displayRow in displayRows)
            _rawRowsByDisplay[displayRow.DisplayValues] = displayRow.RawValues;
    }

    private void Reset()
    {
        _rows.Clear();
        _result = null;
        _target = null;
        _tracker = null;
        _columnTypes = [];
        IsEditable = false;
        ReadOnlyReason = null;
        HasChanges = false;
        StatusMessage = string.Empty;
    }

    private EditableRow NewRow(IReadOnlyList<object?> values)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < _target!.SourceColumns.Count; i++)
            dictionary[_target.SourceColumns[i]] = i < values.Count ? values[i] : null;
        return new EditableRow(dictionary);
    }

    /// <summary>The editable row behind a grid row's display values, or null when the result is read-only.</summary>
    public EditableRow? RowFor(IReadOnlyList<string>? displayValues) =>
        displayValues != null && _rawRowsByDisplay.TryGetValue(displayValues, out var rawRow) && _rows.TryGetValue(rawRow, out var row)
            ? row
            : null;

    public ColumnDataType ColumnTypeAt(int ordinal) =>
        ordinal >= 0 && ordinal < _columnTypes.Length ? _columnTypes[ordinal] : ColumnDataType.Unknown;

    /// <summary>
    /// Binary cells are edited in the table tab's blob editor, not as text here. Nothing is editable
    /// while a commit runs: its success makes the committed values the rows' new originals.
    /// </summary>
    public bool CanEditColumn(int ordinal) => IsEditable && !IsCommitting && ColumnTypeAt(ordinal) != ColumnDataType.Blob;

    public object? GetValue(EditableRow row, int ordinal) => row.Values.GetValueOrDefault(_target!.SourceColumns[ordinal]);

    /// <summary>A grid row's values with its uncommitted edits applied, or null when the editor doesn't hold the row.</summary>
    public IReadOnlyList<object?>? CurrentValuesOf(IReadOnlyList<string> displayValues) =>
        RowFor(displayValues) is { } row ? Enumerable.Range(0, _target!.SourceColumns.Count).Select(i => GetValue(row, i)).ToList() : null;

    public bool IsCellChanged(EditableRow row, int ordinal) => row.IsValueChanged(_target!.SourceColumns[ordinal]);

    /// <summary>
    /// Parses <paramref name="text"/> for the column's type into the cell; returns why it was rejected,
    /// or null. The editor shows NULL as empty text, so empty text leaves a NULL cell NULL.
    /// </summary>
    public string? SetCellText(EditableRow row, int ordinal, string? text)
    {
        if (!CanEditColumn(ordinal))
            return "This column can't be edited here.";
        if (string.IsNullOrEmpty(text) && GetValue(row, ordinal) is null)
            return null;

        try
        {
            SetValue(row, ordinal, CellValueConverter.Parse(text, ColumnTypeAt(ordinal)));
            return null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            StatusMessage = $"⚠️ {ex.Message}";
            return ex.Message;
        }
    }

    public void SetCellNull(EditableRow row, int ordinal)
    {
        if (CanEditColumn(ordinal))
            SetValue(row, ordinal, null);
    }

    /// <summary>Stores a value already converted for the column; Set Value and Fill Down use it.</summary>
    public void SetCellValue(EditableRow row, int ordinal, object? value)
    {
        if (CanEditColumn(ordinal)) SetValue(row, ordinal, value);
    }

    private void SetValue(EditableRow row, int ordinal, object? value)
    {
        var column = _target!.SourceColumns[ordinal];
        row.Values[column] = value;
        if (row.State == EditState.Unchanged && row.IsValueChanged(column))
            row.State = EditState.Modified;
        else if (row.State == EditState.Modified && row.Values.Keys.All(c => !row.IsValueChanged(c)))
            row.State = EditState.Unchanged;
        RefreshHasChanges();
    }

    private void RefreshHasChanges() => HasChanges = _rows.Values.Any(r => r.IsDirty);

    private bool CanRequestCommit => HasChanges && !IsCommitting;

    [RelayCommand(CanExecute = nameof(CanRequestCommit))]
    private void RequestCommit()
    {
        if (_tracker == null)
            return;

        var dirty = _rows.Values.Where(r => r.IsDirty).ToList();
        CommitPreviewRequested?.Invoke(new CommitPreviewRequest(_tracker.GenerateSqlPreview(dirty), _commitSummary));
    }

    /// <summary>Runs the pending edits as one transaction on the tab's session (enlisting in an open one).</summary>
    public async Task ExecuteCommitAsync()
    {
        if (_tracker == null || IsCommitting)
            return;

        var dirty = _rows.Where(pair => pair.Value.IsDirty).ToList();
        var rowCommands = dirty
            .Select(pair => (pair.Value, Command: _tracker.BuildCommand(pair.Value)))
            .Where(rc => rc.Command != null)
            .ToList();
        if (rowCommands.Count == 0)
            return;

        var committing = _result!;
        ConcurrencyConflictRequest? conflict = null;
        IsCommitting = true;
        try
        {
            var session = await _openSession();
            var result = await session.ExecuteTransactionAsync(rowCommands.Select(rc => rc.Command!).ToList());
            if (!ReferenceEquals(_result, committing))
                return; // a newer result replaced these rows while the commit ran

            if (!result.Succeeded)
            {
                StatusMessage = result.IsConcurrencyConflict && result.Outcome == TransactionOutcome.RolledBack
                    ? "⚠️ Commit conflict: a row changed since the query ran. Nothing was saved."
                    : $"❌ Commit failed: {result.DiagnosticMessage}";
                var conflicted = rowCommands.FirstOrDefault(rc => ReferenceEquals(rc.Command, result.FailedCommand)).Value;
                if (result.IsConcurrencyConflict && result.Outcome == TransactionOutcome.RolledBack && conflicted != null)
                    conflict = await BuildConflictRequestAsync(session, conflicted);
            }
            else
            {
                foreach (var (rawRow, row) in dirty)
                    AcceptRow(rawRow, row.Values);
                StatusMessage = result.Outcome == TransactionOutcome.Pending
                    ? $"✅ {dirty.Count} row(s) applied to the open transaction; commit is still pending."
                    : $"✅ {dirty.Count} row(s) saved.";
                RefreshHasChanges();
                RowsRefreshed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Commit failed: {ex.Message}";
        }
        finally
        {
            IsCommitting = false;
            _afterResultChanged?.Invoke();
        }

        if (conflict != null)
            ConcurrencyConflictRequested?.Invoke(conflict);
    }

    /// <summary>
    /// Makes <paramref name="values"/> the row's new originals, so further edits start from them, and
    /// writes them into the result itself, so the tab's copy, sort, filter and export see them too.
    /// </summary>
    private void AcceptRow(object?[] rawRow, IReadOnlyDictionary<string, object?> values)
    {
        _rows[rawRow] = new EditableRow(new Dictionary<string, object?>(values, StringComparer.Ordinal));
        for (var i = 0; i < rawRow.Length && i < _target!.SourceColumns.Count; i++)
            rawRow[i] = values.GetValueOrDefault(_target.SourceColumns[i]);
    }

    /// <summary>RAIN-P2-12 — Looks the conflicted row up by its key, to tell a row someone else changed from one that is gone.</summary>
    private async Task<ConcurrencyConflictRequest> BuildConflictRequestAsync(IDbSession session, EditableRow row)
    {
        var current = await LoadCurrentRowAsync(session, row);
        return current is { HasError: false, ObjectRows.Count: 1 }
            ? new ConcurrencyConflictRequest(
                row,
                "Someone else changed this row since the query ran. Reload it to see their version and drop your edit, or overwrite their change with yours.",
                CanOverwrite: true)
            : new ConcurrencyConflictRequest(
                row,
                "This row was deleted, or its key changed, since the query ran. Reload it to drop your edit.",
                CanOverwrite: false);
    }

    private Task<QueryResult> LoadCurrentRowAsync(IDbSession session, EditableRow row)
    {
        var query = _tracker!.BuildRowQuery(row);
        return session.ExecuteQueryAsync(query.Sql, new QueryExecutionOptions { Parameters = query.Parameters });
    }

    /// <summary>
    /// RAIN-P2-12 — Resolves a conflict by dropping the row's edit and showing its current values. The
    /// grid can't drop a row from the result, so a row that is gone keeps its loaded values until the
    /// query runs again.
    /// </summary>
    public async Task ReloadConflictedRowAsync(EditableRow row)
    {
        var rawRow = _rows.FirstOrDefault(pair => ReferenceEquals(pair.Value, row)).Key;
        if (rawRow == null || IsCommitting)
            return;

        try
        {
            var current = await LoadCurrentRowAsync(await _openSession(), row);
            if (current.HasError)
            {
                StatusMessage = $"❌ Could not reload the row: {current.ErrorMessage}";
                return;
            }

            if (current.ObjectRows.Count != 1)
            {
                row.Revert();
                StatusMessage = "The row no longer exists. Your edit was dropped; run the query again to refresh the result.";
            }
            else
            {
                var values = new Dictionary<string, object?>(row.OriginalValues, StringComparer.Ordinal);
                foreach (var column in _target!.SourceColumns)
                {
                    var ordinal = current.IndexOfColumn(column);
                    if (ordinal >= 0)
                        values[column] = current.ObjectRows[0][ordinal];
                }

                AcceptRow(rawRow, values);
                StatusMessage = "Row reloaded from the database.";
            }

            RefreshHasChanges();
            RowsRefreshed?.Invoke();
            _afterResultChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Could not reload the row: {ex.Message}";
        }
    }

    /// <summary>RAIN-P2-12 — Resolves a conflict by committing again with the row found by its key alone, replacing their change.</summary>
    public async Task OverwriteConflictedRowAsync(EditableRow row)
    {
        if (!_rows.ContainsValue(row) || row.State != EditState.Modified)
            return;

        row.OverwritesConcurrentChanges = true;
        await ExecuteCommitAsync();
    }

    /// <summary>
    /// Before something replaces the result: true when there are no pending edits, or the user
    /// agreed to discard them (they are then reverted). With no way to ask, the edits are kept.
    /// </summary>
    public async Task<bool> ConfirmDiscardingChangesAsync()
    {
        if (!HasChanges)
            return true;
        if (DiscardConfirmation == null || !await DiscardConfirmation(_rows.Values.Count(r => r.IsDirty)))
            return false;

        DiscardChanges();
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void DiscardChanges()
    {
        foreach (var row in _rows.Values.Where(r => r.IsDirty))
            row.Revert();
        StatusMessage = string.Empty;
        RefreshHasChanges();
        RowsRefreshed?.Invoke();
    }
}
