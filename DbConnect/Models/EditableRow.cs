using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>Tracks the persistence state of a row in the editable data grid.</summary>
public enum EditState
{
    Unchanged,
    Modified,
    Inserted,
    Deleted
}

/// <summary>
/// RAIN-202 — Wraps a database row dictionary with change-tracking state.
/// The DataGrid binds to instances of this class so the view can apply
/// per-row styling based on <see cref="State"/>.
/// </summary>
public partial class EditableRow : ObservableObject
{
    /// <summary>The live (possibly edited) cell values, keyed by column name.</summary>
    public Dictionary<string, object?> Values { get; }

    /// <summary>
    /// A snapshot of the original values at load time.
    /// Used by <c>DataChangeTracker</c> to build WHERE clauses and by Revert.
    /// </summary>
    public IReadOnlyDictionary<string, object?> OriginalValues { get; }

    /// <summary>
    /// RAIN-P2-10 — Columns the user explicitly set on a new (<see cref="EditState.Inserted"/>) row.
    /// <c>DataChangeTracker</c> writes only these columns, so identity/generated/default columns the
    /// user never touched are left out of the INSERT and apply server-side.
    /// RAIN-P2-11 — <see cref="Revert"/> and <see cref="RevertColumn"/> reset it to its construction-time contents.
    /// </summary>
    public HashSet<string> EditedColumns { get; } = new(System.StringComparer.Ordinal);

    // RAIN-P2-11 — The columns seeded as edited at construction (e.g. a duplicated row's copied
    // values), so a revert restores EditedColumns to this baseline rather than emptying it.
    private readonly HashSet<string> _originalEditedColumns = new(System.StringComparer.Ordinal);

    [ObservableProperty]
    private EditState _state = EditState.Unchanged;

    /// <summary>
    /// RAIN-P2-12 — The user chose to overwrite whatever changed underneath this row since it loaded,
    /// so its UPDATE identifies the row by key alone instead of also checking the edited cells'
    /// original values. <see cref="Revert"/> clears it.
    /// </summary>
    public bool OverwritesConcurrentChanges { get; set; }

    public EditableRow(Dictionary<string, object?> values, IEnumerable<string>? editedColumns = null)
    {
        Values = values;
        // Copy the originals, keeping the caller's key comparer, so edits don't mutate the snapshot
        OriginalValues = new Dictionary<string, object?>(values, values.Comparer);
        if (editedColumns != null)
        {
            EditedColumns.UnionWith(editedColumns);
            _originalEditedColumns.UnionWith(EditedColumns);
        }
    }

    /// <summary>Records that the user explicitly set <paramref name="column"/> on this row.</summary>
    public void MarkColumnEdited(string column) => EditedColumns.Add(column);

    /// <summary>Convenience: is this row carrying unsaved changes of any kind?</summary>
    public bool IsDirty => State != EditState.Unchanged;
    public string StateDisplay => State switch
    {
        EditState.Inserted => "New",
        EditState.Modified => "Edited",
        EditState.Deleted => "Deleted",
        _ => string.Empty
    };

    /// <summary>
    /// Revert live values back to the original snapshot and clear dirty state.
    /// </summary>
    public void Revert()
    {
        foreach (var kv in OriginalValues)
            Values[kv.Key] = kv.Value;
        EditedColumns.Clear();
        EditedColumns.UnionWith(_originalEditedColumns);
        OverwritesConcurrentChanges = false;
        State = EditState.Unchanged;
    }

    /// <summary>
    /// RAIN-P2-11 — Whether the cell's live value differs from its original, per
    /// <see cref="CellValueEquality"/> (binary by content, everything else exact). The UPDATE's SET
    /// clause uses this alone; <see cref="IsColumnChanged"/> also counts an edited-column change.
    /// </summary>
    public bool IsValueChanged(string column) =>
        !CellValueEquality.AreEqual(Values.GetValueOrDefault(column), OriginalValues.GetValueOrDefault(column));

    /// <summary>RAIN-P2-11 — Does <paramref name="column"/> carry a pending edit that
    /// <see cref="RevertColumn"/> would undo?</summary>
    public bool IsColumnChanged(string column) =>
        IsValueChanged(column)
        || EditedColumns.Contains(column) != _originalEditedColumns.Contains(column);

    /// <summary>
    /// RAIN-P2-11 — Restores one cell to its original value. A <see cref="EditState.Modified"/> row
    /// whose last differing cell this was drops back to <see cref="EditState.Unchanged"/>; an
    /// inserted or deleted row keeps its state.
    /// </summary>
    public void RevertColumn(string column)
    {
        if (OriginalValues.TryGetValue(column, out var original))
            Values[column] = original;

        if (_originalEditedColumns.Contains(column))
            EditedColumns.Add(column);
        else
            EditedColumns.Remove(column);

        if (State == EditState.Modified && OriginalValues.Keys.All(c => !IsColumnChanged(c)))
            State = EditState.Unchanged;
    }

    partial void OnStateChanged(EditState value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(StateDisplay));
    }
}
