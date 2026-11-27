using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// RAIN-P2-16 — Results-grid columns for an editable query result. Grid rows stay the tab's display
/// rows (so its filter and sort still apply); each cell reads and writes the
/// <see cref="Models.EditableRow"/> the editor maps that row to.
/// </summary>
internal static class EditableResultColumns
{
    public static DataGridColumn BuildReadOnly(string header, int ordinal) => new DataGridTemplateColumn
    {
        Header = header,
        IsReadOnly = true,
        CellTemplate = new FuncDataTemplate<IReadOnlyList<string>>(
            (row, _) => BuildCell(null, row, ordinal), supportsRecycling: true),
    };

    public static DataGridColumn Build(QueryResultEditorViewModel editor, string header, int ordinal) => new DataGridTemplateColumn
    {
        Header = header,
        IsReadOnly = !editor.CanEditColumn(ordinal),
        CellTemplate = new FuncDataTemplate<IReadOnlyList<string>>(
            (row, _) => BuildCell(editor, row, ordinal), supportsRecycling: true),
        CellEditingTemplate = new FuncDataTemplate<IReadOnlyList<string>>(
            (row, _) => BuildEditor(editor, row, ordinal), supportsRecycling: false),
    };

    /// <summary>
    /// Cells are built from the editor's rows rather than bound, so a value changed outside a cell
    /// edit (Set NULL, a commit, a discard) only shows once the visible cells are rebuilt.
    /// </summary>
    public static void RefreshCells(DataGrid grid, QueryResultEditorViewModel editor, IReadOnlyDictionary<DataGridColumn, int> ordinals)
    {
        foreach (var gridRow in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            foreach (var (column, ordinal) in ordinals)
            {
                if (column is DataGridTemplateColumn && column.GetCellContent(gridRow)?.Parent is DataGridCell cell)
                    cell.Content = BuildCell(editor, gridRow.DataContext as IReadOnlyList<string>, ordinal);
            }
        }
    }

    private static Control BuildCell(QueryResultEditorViewModel? editor, IReadOnlyList<string>? displayRow, int ordinal)
    {
        var cell = new ContentControl { Content = BuildCellContent(editor, displayRow, ordinal) };
        cell.DataContextChanged += (_, _) =>
            cell.Content = BuildCellContent(editor, cell.DataContext as IReadOnlyList<string>, ordinal);
        return cell;
    }

    private static Control BuildCellContent(QueryResultEditorViewModel? editor, IReadOnlyList<string>? displayRow, int ordinal)
    {
        var row = editor?.RowFor(displayRow);
        var raw = displayRow != null ? QueryResultDisplayRow.RawValuesOf(displayRow) : null;
        var text = new TextBlock
        {
            Text = row != null
                ? GridCellDisplay.FormatValue(editor!.GetValue(row, ordinal))
                : raw != null && ordinal < raw.Count ? GridCellDisplay.FormatValue(raw[ordinal])
                : displayRow != null && ordinal < displayRow.Count ? GridCellDisplay.FormatValue(displayRow[ordinal]) : string.Empty,
            Padding = new Avalonia.Thickness(6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        if (row != null && editor!.IsCellChanged(row, ordinal))
        {
            text.FontWeight = FontWeight.SemiBold;
            text.Bind(TextBlock.BackgroundProperty, text.GetResourceObservable("DbConnectModifiedRowBrush"));
        }

        return text;
    }

    /// <summary>The typed text is written back by the view when the grid commits the edit, never on focus loss.</summary>
    private static Control BuildEditor(QueryResultEditorViewModel editor, IReadOnlyList<string>? displayRow, int ordinal)
    {
        var row = editor.RowFor(displayRow);
        return new TextBox
        {
            Text = row != null ? CellValueConverter.FormatForEditor(editor.GetValue(row, ordinal), editor.ColumnTypeAt(ordinal)) : string.Empty,
            Watermark = row != null && editor.GetValue(row, ordinal) is null ? "NULL" : null,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
    }
}
