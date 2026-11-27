using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class QueryResultView : UserControl
{
    private IReadOnlyList<QueryResultDisplayRow> _displayRows = [];
    /// <summary>Maps each generated column back to its data ordinal, independent of visual reordering.</summary>
    private readonly Dictionary<DataGridColumn, int> _columnOrdinals = new();
    private int _lastClickedOrdinal = -1;
    private string? _lastClickedColumnName;

    public QueryResultView()
    {
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("MainGrid")!;
        grid.CellPointerPressed += (_, e) =>
        {
            _lastClickedOrdinal = e.Column != null && _columnOrdinals.TryGetValue(e.Column, out var ordinal) ? ordinal : -1;
            _lastClickedColumnName = e.Column?.Header as string;
        };

        var contextMenu = new ContextMenu();
        var viewValueItem = new MenuItem { Header = "View Value…" };
        viewValueItem.Click += OnViewValueClicked;
        contextMenu.Items.Add(viewValueItem);
        var viewBinaryItem = new MenuItem { Header = "View Binary…" };
        viewBinaryItem.Click += OnViewBinaryClicked;
        contextMenu.Items.Add(viewBinaryItem);
        contextMenu.Opening += (_, e) =>
        {
            var value = ClickedValue();
            viewBinaryItem.IsVisible = value is byte[];
            viewValueItem.IsVisible = CellValueViewerViewModel.TextOf(value) != null;
            e.Cancel = !viewBinaryItem.IsVisible && !viewValueItem.IsVisible;
        };
        grid.ContextMenu = contextMenu;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is QueryResult result)
            UpdateGrid(result);
    }

    private void UpdateGrid(QueryResult result)
    {
        var grid = this.FindControl<DataGrid>("MainGrid")!;
        grid.Columns.Clear();
        grid.ItemsSource = null;
        _columnOrdinals.Clear();

        if (result == null || result.HasError) return;

        _displayRows = QueryResultFormattingService.BuildDisplayRows(result);

        for (var i = 0; i < result.ColumnInfos.Count; i++)
        {
            var ordinal = i;
            var column = new DataGridTemplateColumn
            {
                Header = result.ColumnInfos[i].Name,
                CanUserResize = true,
                CellTemplate = new FuncDataTemplate<QueryResultDisplayRow>(
                    (row, _) => BuildRecycledCell(row, ordinal), supportsRecycling: true)
            };
            grid.Columns.Add(column);
            _columnOrdinals[column] = ordinal;
        }

        grid.ItemsSource = _displayRows;
    }

    private static Control BuildRecycledCell(QueryResultDisplayRow row, int ordinal)
    {
        var cell = new ContentControl { Content = BuildResultCell(row, ordinal) };
        cell.DataContextChanged += (_, _) =>
        {
            if (cell.DataContext is QueryResultDisplayRow current)
                cell.Content = BuildResultCell(current, ordinal);
        };
        return cell;
    }

    private static Control BuildResultCell(QueryResultDisplayRow row, int ordinal)
    {
        var rawValue = ordinal < row.RawValues.Length ? row.RawValues[ordinal] : null;

        if (rawValue is byte[] bytes && bytes.Length > 0)
        {
            var fmt = ImageFormatDetector.Detect(bytes);
            if (fmt != null)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    return new Image
                    {
                        Source = new Bitmap(ms),
                        Height = 24,
                        Stretch = Stretch.Uniform,
                        Margin = new Avalonia.Thickness(2, 1),
                    };
                }
                catch { /* fall through to text */ }
            }
        }

        var text = GridCellDisplay.FormatValue(rawValue);

        return new TextBlock
        {
            Text = text,
            Padding = new Avalonia.Thickness(6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>The raw value of the cell last clicked in the selected row, or null.</summary>
    private object? ClickedValue()
    {
        var ordinal = _lastClickedOrdinal;
        return this.FindControl<DataGrid>("MainGrid")!.SelectedItem is QueryResultDisplayRow row &&
               ordinal >= 0 && ordinal < row.RawValues.Length
            ? row.RawValues[ordinal]
            : null;
    }

    private async void OnViewValueClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            await CellValueViewerWindow.ShowAsync(window, ClickedValue(), _lastClickedColumnName ?? "value");
    }

    private async void OnViewBinaryClicked(object? sender, RoutedEventArgs e)
    {
        if (ClickedValue() is not byte[] bytes || TopLevel.GetTopLevel(this) is not Window window)
            return;

        var dialog = new BlobEditorDialog(bytes, _lastClickedColumnName ?? "value", allowEdit: false);
        await dialog.ShowDialog(window);
    }
}
