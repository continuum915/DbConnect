using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Adds Set Value, Fill Down and Form View to a result grid's context menu, set in XAML with
/// <c>views:GridEditMenu.Mode="Query"</c> or <c>"Table"</c>. The selection is the selected rows in the current
/// column; edits go through the grid's own setters and appear as ordinary pending edits.
/// </summary>
public static class GridEditMenu
{
    public static readonly AttachedProperty<string?> ModeProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, string?>("Mode", typeof(GridEditMenu));

    public static string? GetMode(DataGrid grid) => grid.GetValue(ModeProperty);
    public static void SetMode(DataGrid grid, string? value) => grid.SetValue(ModeProperty, value);

    static GridEditMenu()
    {
        ModeProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is not string) return;
            grid.AttachedToVisualTree += (_, _) => AddItems(grid);
            grid.SelectionChanged += (_, _) => Record(grid)?.SetGridRow(grid.SelectedIndex);
            RecordViewModel? watched = null;
            void OnRecordChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(RecordViewModel.IsActive) && sender is RecordViewModel { IsActive: false })
                    RefreshCells(grid);
            }
            grid.DataContextChanged += (_, _) =>
            {
                if (watched != null) watched.PropertyChanged -= OnRecordChanged;
                watched = Record(grid);
                if (watched != null) watched.PropertyChanged += OnRecordChanged;
            };
        });
    }

    private static RecordViewModel? Record(DataGrid grid) => grid.DataContext switch
    {
        QueryTabViewModel query => query.Record,
        TableDataTabViewModel table => table.Record,
        _ => null
    };

    private static void AddItems(DataGrid grid)
    {
        if (grid.ContextMenu is not { } menu || menu.Items.OfType<MenuItem>().Any(i => Equals(i.Tag, typeof(GridEditMenu))))
            return;
        var setValue = new MenuItem { Header = "Set Value", Tag = typeof(GridEditMenu) };
        foreach (var (header, value) in new (string, CellEditValue?)[]
                 {
                     ("NULL", CellEditValue.Null), ("Empty String", CellEditValue.EmptyString),
                     ("Default", CellEditValue.Default), ("Value…", null)
                 })
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await SetValueAsync(grid, value);
            setValue.Items.Add(item);
        }
        var fillDown = new MenuItem { Header = "Fill Down" };
        fillDown.Click += (_, _) => Report(grid, Apply(grid, fill: true, null));
        var form = new MenuItem { Header = "Form View" };
        form.Click += (_, _) => Record(grid)?.ToggleCommand.Execute(null);
        menu.Items.Add(new Separator());
        menu.Items.Add(setValue);
        menu.Items.Add(fillDown);
        menu.Items.Add(form);
    }

    private static async Task SetValueAsync(DataGrid grid, CellEditValue? value)
    {
        if (value == null)
        {
            if (TopLevel.GetTopLevel(grid) is not Window owner) return;
            var text = await new GroupInputDialog("Value for the selected cells:") { Title = "Set Value" }.ShowDialog<string?>(owner);
            if (text == null) return;
            value = new CellEditValue(CellEditKind.Text, text);
        }
        Report(grid, Apply(grid, fill: false, value));
    }

    /// <summary>Runs Set Value or Fill Down on the grid's selection; returns why nothing changed, or null.</summary>
    internal static string? Apply(DataGrid grid, bool fill, CellEditValue? value)
    {
        if (grid.CurrentColumn is not { } column) return "Select a cell first.";
        var error = ApplyTo(grid.DataContext, grid.SelectedItems.Cast<object>().ToList(), grid.Columns.IndexOf(column),
            column.Header as string, fill, value);
        RefreshCells(grid);
        return error;
    }

    /// <summary>The grid-independent part of <see cref="Apply"/>: selected row items, the column's index and header.</summary>
    internal static string? ApplyTo(object? context, IReadOnlyList<object> selected, int columnIndex, string? columnName,
        bool fill, CellEditValue? value)
    {
        switch (context)
        {
            case QueryTabViewModel tab:
                {
                    var editor = tab.ResultEditor;
                    if (!editor.CanEditColumn(columnIndex)) return editor.ReadOnlyReason ?? "This column can't be edited here.";
                    var rows = selected.OfType<IReadOnlyList<string>>()
                        .OrderBy(r => tab.DisplayResultRows.IndexOf(r))
                        .Select(editor.RowFor).OfType<EditableRow>().ToList();
                    return fill
                        ? GridCellEdits.FillDown(rows, r => editor.GetValue(r, columnIndex), (r, v) => editor.SetCellValue(r, columnIndex, v))
                        : GridCellEdits.SetValues(rows, value!, editor.ColumnTypeAt(columnIndex), tab.SelectedSession?.Capabilities,
                            (r, v) => editor.SetCellValue(r, columnIndex, v));
                }
            case TableDataTabViewModel tab when columnName != null:
                {
                    var source = new TableRecordSource(tab);
                    var rows = selected.OfType<EditableRow>().OrderBy(tab.Rows.IndexOf).ToList();
                    var ordinal = source.Columns.ToList().IndexOf(columnName);
                    if (rows.Any(r => !source.CanEdit(tab.Rows.IndexOf(r), ordinal)))
                        return "Some selected rows can't be edited: enable all-column identity for a table without a primary key.";
                    return fill
                        ? GridCellEdits.FillDown(rows, r => r.Values.GetValueOrDefault(columnName), (r, v) => source.Set(r, columnName, v))
                        : GridCellEdits.SetValues(rows, value!, tab.GetColumnType(columnName), tab.Session?.Capabilities,
                            (r, v) => source.Set(r, columnName, v));
                }
            default:
                return "Select a cell first.";
        }
    }

    /// <summary>Rebuilds visible cells after edits made outside the grid (Form View).</summary>
    private static void RefreshCells(DataGrid grid)
    {
        switch (grid.DataContext)
        {
            case QueryTabViewModel tab:
                EditableResultColumns.RefreshCells(grid, tab.ResultEditor,
                    grid.Columns.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i));
                break;
            case TableDataTabViewModel tab:
                foreach (var row in tab.Rows)
                    foreach (var column in grid.Columns.OfType<DataGridTemplateColumn>())
                        if (column.Header is string name && column.GetCellContent(row)?.Parent is DataGridCell cell)
                            cell.Content = TableCellControls.BuildTableCell(row, name);
                break;
        }
    }

    private static void Report(DataGrid grid, string? error)
    {
        if (error == null) return;
        switch (grid.DataContext)
        {
            case QueryTabViewModel tab: tab.ResultEditor.StatusMessage = $"⚠️ {error}"; break;
            case TableDataTabViewModel tab: tab.StatusMessage = $"⚠️ {error}"; break;
        }
    }
}
