using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Shared grid editing, preview, transactions and conflict resolution.
/// Hosts supply result state, session lifetime and reload policy.</summary>
public sealed class ResultEditingViewModel
{
    private readonly IResultEditingHost _host;
    public ResultEditingViewModel(IResultEditingHost host) => _host = host;
    public DataChangeTracker? Tracker { get; set; }
    private ObservableCollection<EditableRow> Rows => _host.Rows;
    private ObservableCollection<string> Columns => _host.Columns;
    private EditableRow? SelectedRow { get => _host.SelectedRow; set => _host.SelectedRow = value; }
    private bool IsCommitting { get => _host.IsCommitting; set => _host.IsCommitting = value; }
    private bool CanEditExistingRows => _host.CanEditExistingRows;
    private string CommitModeSummary => _host.CommitModeSummary;
    private Task<IDbSession> GetOrOpenDbSessionAsync() => _host.OpenEditingSessionAsync();
    private void RefreshDirtyState() => _host.RefreshEditingState();
    private void SetBaseStatus(string? message) => _host.SetEditingStatus(message);

    public void MarkRowModified(EditableRow row, string? columnName = null)
    {
        if (row.State == EditState.Inserted || row.State == EditState.Deleted)
        {
            if (row.State == EditState.Inserted && columnName != null)
                row.MarkColumnEdited(columnName);
            RefreshDirtyState();
            return;
        }

        if (!CanEditExistingRows)
        {
            SetBaseStatus("⚠️ Existing-row edits require a primary key or enabling all-column identity fallback for this tab.");
            // The rejected edit has already ended, so there's no open editor to cancel first.
            row.Revert();
            OnRowValuesReverted([row]);
            return;
        }

        if (row.State == EditState.Unchanged)
            row.State = EditState.Modified;

        RefreshDirtyState();
    }

    public void AddRow()
    {
        var blank = Columns.ToDictionary(col => col, _ => (object?)null, StringComparer.Ordinal);
        Rows.Add(new EditableRow(blank) { State = EditState.Inserted });
        SelectedRow = Rows.LastOrDefault();
        RefreshDirtyState();
    }

    public void DeleteRow(EditableRow? row)
    {
        if (row == null)
            return;

        if (row.State == EditState.Inserted)
        {
            Rows.Remove(row);
        }
        else
        {
            if (!CanEditExistingRows)
            {
                SetBaseStatus("⚠️ Existing-row delete requires a primary key or enabling all-column identity fallback for this tab.");
                RefreshDirtyState();
                return;
            }

            row.State = EditState.Deleted;
        }

        RefreshDirtyState();
    }

    public void DuplicateSelectedRow()
    {
        if (SelectedRow == null)
            return;

        var clone = Columns.ToDictionary(
            column => column,
            column => SelectedRow.Values.GetValueOrDefault(column),
            StringComparer.Ordinal);

        foreach (var pk in _host.PrimaryKeyColumns)
            clone[pk] = null;

        // The duplicated non-PK values are an explicit choice to carry them into the new row; the
        // reset PK stays untouched so an identity/auto-increment column still applies server-side.
        var editedColumns = Columns.Except(_host.PrimaryKeyColumns, StringComparer.Ordinal);
        var row = new EditableRow(clone, editedColumns) { State = EditState.Inserted };
        Rows.Add(row);
        SelectedRow = row;
        RefreshDirtyState();
    }

    public Task CommitAsync()
    {
        var dirty = Rows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            SetBaseStatus("No changes to commit.");
            return Task.CompletedTask;
        }

        if (Tracker == null)
        {
            SetBaseStatus("❌ Change tracker not initialized. Please refresh first.");
            return Task.CompletedTask;
        }

        if (!CanEditExistingRows && dirty.Any(row => row.State is EditState.Modified or EditState.Deleted))
        {
            SetBaseStatus("⚠️ Existing-row updates and deletes are blocked until you enable all-column identity fallback for this tab.");
            return Task.CompletedTask;
        }

        var preview = Tracker.GenerateSqlPreview(dirty);
        CommitPreviewRequested?.Invoke(new CommitPreviewRequest(preview, CommitModeSummary));
        return Task.CompletedTask;
    }

    public async Task ExecuteCommitAsync()
    {
        if (Tracker == null || !_host.HasConnection)
            return;

        var dirty = Rows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0)
            return;

        var selectionIdentity = BuildIdentitySnapshot(SelectedRow);
        ConcurrencyConflictRequest? conflict = null;
        IsCommitting = true;
        SetBaseStatus("Committing...");
        try
        {
            var rowCommands = dirty
                .Select(row => (Row: row, Command: Tracker.BuildCommand(row)))
                .Where(pair => pair.Command != null)
                .ToList();
            var commands = rowCommands.Select(pair => pair.Command!).ToList();

            var dbSession = await GetOrOpenDbSessionAsync();
            var result = await dbSession.ExecuteTransactionAsync(commands);
            if (!result.Succeeded)
            {
                var prefix = result.IsConcurrencyConflict ? "⚠️ Commit conflict" : "❌ Commit failed";
                SetBaseStatus(string.IsNullOrWhiteSpace(result.FailedStatement)
                    ? $"{prefix}: {result.DiagnosticMessage}"
                    : $"{prefix}: {result.DiagnosticMessage} Statement: {result.FailedStatement}");

                var conflictedRow = rowCommands.FirstOrDefault(pair => ReferenceEquals(pair.Command, result.FailedCommand)).Row;
                if (result.IsConcurrencyConflict && result.Outcome == TransactionOutcome.RolledBack && conflictedRow != null)
                    conflict = await BuildConflictRequestAsync(dbSession, conflictedRow);
            }
            else
            {
                SetBaseStatus(result.Outcome == TransactionOutcome.Pending
                    ? $"✅ {dirty.Count} change(s) applied to the open transaction; commit is still pending."
                    : $"✅ {dirty.Count} change(s) committed successfully.");
                await _host.ReloadResultsAsync();
                if (selectionIdentity != null)
                    SelectedRow = FindMatchingRow(selectionIdentity) ?? SelectedRow;
            }
        }
        catch (ObjectDisposedException)
        {
            // The tab was closed while committing.
        }
        finally
        {
            IsCommitting = false;
            RefreshDirtyState();
        }

        if (conflict != null)
            ConcurrencyConflictRequested?.Invoke(conflict);
    }

    private async Task<ConcurrencyConflictRequest> BuildConflictRequestAsync(IDbSession dbSession, EditableRow row)
    {
        var current = await LoadCurrentRowAsync(dbSession, row);
        return current is { HasError: false, ObjectRows.Count: 1 }
            ? new ConcurrencyConflictRequest(
                row,
                "Someone else changed this row since it was loaded. Reload it to see their version and drop your edit, or overwrite their change with yours.",
                CanOverwrite: row.State == EditState.Modified)
            : new ConcurrencyConflictRequest(
                row,
                "This row was deleted, or changed so it can no longer be identified, since it was loaded. Reload it to drop your edit.",
                CanOverwrite: false);
    }

    private Task<QueryResult> LoadCurrentRowAsync(IDbSession dbSession, EditableRow row)
    {
        var query = Tracker!.BuildRowQuery(row);
        return dbSession.ExecuteQueryAsync(query.Sql, new QueryExecutionOptions { Parameters = query.Parameters });
    }

    public async Task ReloadConflictedRowAsync(EditableRow row)
    {
        var index = Rows.IndexOf(row);
        if (Tracker == null || index < 0)
            return;

        try
        {
            var current = await LoadCurrentRowAsync(await GetOrOpenDbSessionAsync(), row);
            if (current.HasError)
            {
                SetBaseStatus($"❌ Could not reload the row: {current.ErrorMessage}");
                return;
            }

            RevertStarting?.Invoke();
            if (current.ObjectRows.Count == 0)
            {
                Rows.RemoveAt(index);
                OnRowValuesReverted([]);
                SetBaseStatus("The row no longer exists and was removed from the grid.");
                return;
            }

            if (current.ObjectRows.Count > 1)
            {
                // Several rows match its loaded values (a keyless table), so drop the edit in place.
                row.Revert();
                OnRowValuesReverted([row]);
                SetBaseStatus("Row changes reverted.");
                return;
            }

            var reloaded = new EditableRow(current.ToRowDictionary(current.ObjectRows[0]));
            var wasSelected = ReferenceEquals(SelectedRow, row);
            Rows[index] = reloaded;
            if (wasSelected)
                SelectedRow = reloaded;
            OnRowValuesReverted([reloaded]);
            SetBaseStatus("Row reloaded from the database.");
        }
        catch (ObjectDisposedException)
        {
            // The tab was closed while reloading.
        }
        catch (Exception ex)
        {
            SetBaseStatus($"❌ Could not reload the row: {ex.Message}");
        }
    }

    public async Task OverwriteConflictedRowAsync(EditableRow row)
    {
        if (!Rows.Contains(row) || row.State != EditState.Modified)
            return;

        row.OverwritesConcurrentChanges = true;
        await ExecuteCommitAsync();
    }

    public void Revert()
    {
        RevertStarting?.Invoke();
        var stillInGrid = new List<EditableRow>();
        foreach (var row in Rows.Where(r => r.IsDirty).ToList())
        {
            if (RevertOrRemoveRow(row))
                stillInGrid.Add(row);
        }

        OnRowValuesReverted(stillInGrid);
        SetBaseStatus("All changes reverted.");
    }

    public bool CanRevertCell([NotNullWhen(true)] EditableRow? row, [NotNullWhen(true)] string? column) =>
        !IsCommitting && row is { State: not EditState.Deleted } && column != null && row.IsColumnChanged(column);

    public void RevertCell(EditableRow row, string column)
    {
        if (!CanRevertCell(row, column))
            return;

        RevertStarting?.Invoke();
        row.RevertColumn(column);
        OnRowValuesReverted([row]);
        SetBaseStatus($"Reverted {column}.");
    }

    public bool CanRevertRow([NotNullWhen(true)] EditableRow? row) => !IsCommitting && row is { IsDirty: true };

    public void RevertRow(EditableRow? row)
    {
        if (!CanRevertRow(row))
            return;

        RevertStarting?.Invoke();
        OnRowValuesReverted(RevertOrRemoveRow(row) ? [row] : []);
        SetBaseStatus("Row changes reverted.");
    }

    private bool RevertOrRemoveRow(EditableRow row)
    {
        if (row.State == EditState.Inserted)
        {
            Rows.Remove(row);
            return false;
        }

        row.Revert();
        return true;
    }

    public int PasteRowsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Columns.Count == 0)
            return 0;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (lines.Count == 0)
            return 0;

        var headerCells = SplitTabLine(lines[0]);
        var startIndex = headerCells.SequenceEqual(Columns, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
        var inserted = 0;

        for (var i = startIndex; i < lines.Count; i++)
        {
            var cells = SplitTabLine(lines[i]);
            if (cells.Count == 0)
                continue;

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var editedColumns = new List<string>();
            for (var columnIndex = 0; columnIndex < Columns.Count; columnIndex++)
            {
                var raw = columnIndex < cells.Count ? cells[columnIndex] : string.Empty;
                values[Columns[columnIndex]] = ParsePastedValue(raw);
                // Only columns an actual pasted cell supplied count as explicitly set; a short paste
                // leaves the rest at their column default so identity/default columns stay untouched.
                if (columnIndex < cells.Count)
                    editedColumns.Add(Columns[columnIndex]);
            }

            foreach (var pk in _host.PrimaryKeyColumns)
                values[pk] = null;
            editedColumns.RemoveAll(_host.PrimaryKeyColumns.Contains);

            Rows.Add(new EditableRow(values, editedColumns) { State = EditState.Inserted });
            inserted++;
        }

        if (inserted > 0)
        {
            SelectedRow = Rows.LastOrDefault();
            RefreshDirtyState();
            SetBaseStatus($"Pasted {inserted} row(s) into the table editor.");
        }

        return inserted;
    }

    public event Action<CommitPreviewRequest>? CommitPreviewRequested;

    public event Action<ConcurrencyConflictRequest>? ConcurrencyConflictRequested;

    public event Action<IReadOnlyList<EditableRow>>? RowValuesReverted;

    public event Action? RevertStarting;

    private void OnRowValuesReverted(IReadOnlyList<EditableRow> rows)
    {
        RefreshDirtyState();
        RowValuesReverted?.Invoke(rows);
    }

    private static List<string> SplitTabLine(string line) =>
        line.Split('\t').ToList();

    private static object? ParsePastedValue(string value)
    {
        if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (bool.TryParse(value, out var boolValue))
            return boolValue;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            return longValue;

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
            return decimalValue;

        return value;
    }

    public Dictionary<string, object?>? BuildIdentitySnapshot(EditableRow? row)
    {
        if (row == null)
            return null;

        var identityColumns = _host.PrimaryKeyColumns.Count > 0 ? _host.PrimaryKeyColumns : Columns.ToList();
        if (identityColumns.Count == 0)
            return null;

        return identityColumns.ToDictionary(
            column => column,
            column => row.Values.GetValueOrDefault(column),
            StringComparer.Ordinal);
    }

    public EditableRow? FindMatchingRow(IReadOnlyDictionary<string, object?> identity)
    {
        return Rows.FirstOrDefault(row =>
            identity.All(entry => CellValueEquality.AreEqual(row.Values.GetValueOrDefault(entry.Key), entry.Value)));
    }

    public void ObserveRows(ObservableCollection<EditableRow> rows)
    {
        rows.CollectionChanged -= OnRowsCollectionChanged;
        rows.CollectionChanged += OnRowsCollectionChanged;

        foreach (var row in rows)
            row.PropertyChanged -= OnRowPropertyChanged;

        foreach (var row in rows)
            row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (EditableRow row in e.OldItems)
                row.PropertyChanged -= OnRowPropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (EditableRow row in e.NewItems)
                row.PropertyChanged += OnRowPropertyChanged;
        }

        RefreshDirtyState();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableRow.State))
            RefreshDirtyState();
    }

}
