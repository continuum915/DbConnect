using System;
using System.Collections.Generic;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Record view over a query tab's displayed rows; edits go through the result editor.</summary>
internal sealed class QueryRecordSource(QueryTabViewModel tab) : IRecordSource
{
    private IReadOnlyList<string> Row(int row) => tab.DisplayResultRows[row];
    private EditableRow? Editable(int row) => tab.ResultEditor.IsEditable ? tab.ResultEditor.RowFor(Row(row)) : null;

    public int Count => tab.DisplayResultRows.Count;
    public IReadOnlyList<string> Columns => tab.ResultColumns;

    public object? Value(int row, int column) => Editable(row) is { } editable
        ? tab.ResultEditor.GetValue(editable, column)
        : column < Row(row).Count ? Row(row)[column] : null;

    public bool CanEdit(int row, int column) => Editable(row) != null && tab.ResultEditor.CanEditColumn(column);

    public string? SetText(int row, int column, string? text) => Editable(row) is { } editable
        ? tab.ResultEditor.SetCellText(editable, column, text)
        : "This result is read-only.";

    public void SetNull(int row, int column)
    {
        if (Editable(row) is { } editable) tab.ResultEditor.SetCellNull(editable, column);
    }

    public bool IsChanged(int row, int column) => Editable(row) is { } editable && tab.ResultEditor.IsCellChanged(editable, column);
}

/// <summary>Record view over a table tab's loaded rows and visible columns.</summary>
internal sealed class TableRecordSource(TableDataTabViewModel tab) : IRecordSource
{
    public int Count => tab.Rows.Count;
    public IReadOnlyList<string> Columns => tab.DisplayedColumns;

    private string Column(int column) => Columns[column];

    public object? Value(int row, int column) => tab.Rows[row].Values.GetValueOrDefault(Column(column));

    public bool CanEdit(int row, int column) =>
        !tab.IsCommitting && (tab.Rows[row].State == EditState.Inserted || tab.CanEditExistingRows) &&
        tab.GetColumnType(Column(column)) != ColumnDataType.Blob;

    public string? SetText(int row, int column, string? text)
    {
        if (!CanEdit(row, column)) return "This cell can't be edited here.";
        try
        {
            Set(tab.Rows[row], Column(column), CellValueConverter.Parse(text, tab.GetColumnType(Column(column))));
            return null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return ex.Message;
        }
    }

    public void SetNull(int row, int column)
    {
        if (CanEdit(row, column)) Set(tab.Rows[row], Column(column), null);
    }

    public bool IsChanged(int row, int column) => tab.Rows[row].IsValueChanged(Column(column));

    internal void Set(EditableRow row, string column, object? value)
    {
        row.Values[column] = value;
        tab.MarkRowModified(row, column);
    }
}
