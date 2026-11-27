using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Set Value and Fill Down for both result grids. Edits go through the grid's own cell setter, so they become
/// ordinary pending edits with the usual preview, commit, concurrency check and revert. A selection is the
/// selected rows in one column (the grid selects whole rows).
/// </summary>
public static class GridCellEdits
{
    /// <summary>The value to store for <paramref name="edit"/> in a column of <paramref name="type"/>.</summary>
    /// <exception cref="FormatException">The text does not parse for the column, or empty text is not allowed.</exception>
    public static object? Resolve(CellEditValue edit, ColumnDataType type, DatabaseProviderCapabilities? capabilities) => edit.Kind switch
    {
        CellEditKind.Null => null,
        CellEditKind.EmptyString when type is ColumnDataType.Text or ColumnDataType.Unknown => string.Empty,
        CellEditKind.EmptyString => throw new FormatException("Only text columns can hold an empty string."),
        CellEditKind.Default when capabilities?.SupportsUpdateSetDefault == false =>
            throw new FormatException("This database cannot set an existing value back to its column default."),
        CellEditKind.Default => ColumnDefault.Value,
        _ => CellValueConverter.Parse(edit.Text, type)
    };

    /// <summary>
    /// Applies <paramref name="edit"/> to each row through <paramref name="set"/>. Returns why it was refused,
    /// or null when every cell was set. Nothing is changed when the value is invalid for the column.
    /// </summary>
    public static string? SetValues<TRow>(IReadOnlyList<TRow> rows, CellEditValue edit, ColumnDataType type,
        DatabaseProviderCapabilities? capabilities, Action<TRow, object?> set)
    {
        if (rows.Count == 0) return "Select the rows to change.";
        object? value;
        try { value = Resolve(edit, type, capabilities); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return ex.Message; }
        foreach (var row in rows) set(row, value);
        return null;
    }

    /// <summary>Copies the first row's value (in display order) into the other selected rows.</summary>
    public static string? FillDown<TRow>(IReadOnlyList<TRow> rowsInDisplayOrder, Func<TRow, object?> get, Action<TRow, object?> set)
    {
        if (rowsInDisplayOrder.Count < 2) return "Select at least two rows; the top one is copied down.";
        var value = get(rowsInDisplayOrder[0]);
        foreach (var row in rowsInDisplayOrder.Skip(1)) set(row, value is byte[] bytes ? bytes.Clone() : value);
        return null;
    }
}
