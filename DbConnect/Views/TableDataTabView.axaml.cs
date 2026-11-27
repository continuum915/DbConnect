using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class TableDataTabView : UserControl
{
    private TableDataTabViewModel? _vm;

    // revert-grid-fixups — The cell last pressed in the grid. Single-cell/row menu actions target it
    // rather than grid.SelectedItem, which stays on the first selected row when the user
    // right-clicks inside a multi-row selection.
    private (EditableRow Row, string Column)? _clickedCell;

    // revert-grid-fixups — The edited cell's value when its editor opened. Most editors write to
    // Values as the user changes them, so cancelling the edit (Esc, or a revert) restores this.
    private (EditableRow Row, string Column, object? Value)? _editSnapshot;

    public TableDataTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();

        if (DataContext is not TableDataTabViewModel vm)
            return;

        _vm = vm;
        _vm.Columns.CollectionChanged += OnColumnsChanged;
        _vm.Rows.CollectionChanged += OnRowsChanged;
        _vm.CommitPreviewRequested += OnCommitPreviewRequested;
        _vm.ConcurrencyConflictRequested += OnConcurrencyConflictRequested;
        _vm.RevertStarting += OnRevertStarting;
        _vm.RowValuesReverted += OnRowValuesReverted;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ColumnLayoutChanged += OnColumnLayoutChanged;
        AttachRowHandlers(_vm.Rows);

        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid != null)
        {
            grid.BeginningEdit += OnGridBeginningEdit;
            grid.CellEditEnding += OnCellEditEnding;
            grid.CellEditEnded += OnCellEditEnded;
            grid.SelectionChanged += OnGridSelectionChanged;
            grid.KeyDown += OnGridKeyDown;
            grid.Sorting += OnGridSorting;
            grid.ColumnReordered += OnGridColumnReordered;
            grid.AddHandler(InputElement.PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
            grid.CellPointerPressed += OnGridCellPointerPressed;
            if (grid.ContextMenu != null)
                grid.ContextMenu.Opening += OnGridContextMenuOpening;
        }

        var deleteBtn = this.FindControl<Button>("DeleteRowButton");
        if (deleteBtn != null)
            deleteBtn.Click += OnDeleteRowClicked;

        if (_vm.Rows.Count == 0 && !_vm.IsLoading)
            _ = _vm.RefreshAsync();

        RebuildColumns(_vm);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is TableDataTabViewModel vm)
            vm.ExportPathPicker = new StorageProviderExportPathPicker(TopLevel.GetTopLevel(this));
    }

    private void DetachViewModel()
    {
        if (_vm == null)
            return;

        _vm.Columns.CollectionChanged -= OnColumnsChanged;
        _vm.Rows.CollectionChanged -= OnRowsChanged;
        _vm.CommitPreviewRequested -= OnCommitPreviewRequested;
        _vm.ConcurrencyConflictRequested -= OnConcurrencyConflictRequested;
        _vm.RevertStarting -= OnRevertStarting;
        _vm.RowValuesReverted -= OnRowValuesReverted;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.ColumnLayoutChanged -= OnColumnLayoutChanged;
        DetachRowHandlers(_vm.Rows);

        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid != null)
        {
            grid.BeginningEdit -= OnGridBeginningEdit;
            grid.CellEditEnding -= OnCellEditEnding;
            grid.CellEditEnded -= OnCellEditEnded;
            grid.SelectionChanged -= OnGridSelectionChanged;
            grid.KeyDown -= OnGridKeyDown;
            grid.Sorting -= OnGridSorting;
            grid.ColumnReordered -= OnGridColumnReordered;
            grid.RemoveHandler(InputElement.PointerPressedEvent, OnGridPointerPressed);
            grid.CellPointerPressed -= OnGridCellPointerPressed;
            if (grid.ContextMenu != null)
                grid.ContextMenu.Opening -= OnGridContextMenuOpening;
            _clickedCell = null;
            _editSnapshot = null;
        }

        var deleteBtn = this.FindControl<Button>("DeleteRowButton");
        if (deleteBtn != null)
            deleteBtn.Click -= OnDeleteRowClicked;

        _vm = null;
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm != null)
            RebuildColumns(_vm);
    }

    // Posted rather than run inline: a layout change can come from the grid's own ColumnReordered
    // event, and clearing the grid's columns from inside that event would pull them out from under it.
    private void OnColumnLayoutChanged()
    {
        var vm = _vm;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(vm, _vm) && vm != null)
                RebuildColumns(vm);
        });
    }

    private void OnGridColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        // DisplayIndex counts the State column, which the layout doesn't know about.
        if (_vm != null && e.Column.Header is string column)
            _ = _vm.MoveColumnAsync(column, e.Column.DisplayIndex - 1);
    }

    private void OnHideColumnClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && MenuTargetCell() is (_, var column))
            _ = _vm.HideColumnAsync(column);
    }

    private void OnFreezeColumnsClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && MenuTargetCell() is (_, var column))
            _ = _vm.FreezeColumnsThroughAsync(column);
    }

    private void OnUnfreezeColumnsClicked(object? sender, RoutedEventArgs e) => _ = _vm?.UnfreezeColumnsAsync();

    private void OnShowAllColumnsClicked(object? sender, RoutedEventArgs e) => _ = _vm?.ShowAllColumnsAsync();

    // The Columns toolbar flyout: one checkable item per column, checked while it's visible.
    private void OnColumnsFlyoutOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout || _vm == null)
            return;

        var vm = _vm;
        var hidden = vm.HiddenColumns;
        var items = vm.LayoutColumns.Select(column =>
        {
            var item = new MenuItem
            {
                Header = column,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = !hidden.Contains(column),
            };
            item.Click += (_, _) => _ = hidden.Contains(column) ? vm.ShowColumnAsync(column) : vm.HideColumnAsync(column);
            return (object)item;
        }).ToList();

        items.Add(new Separator());
        var showAll = new MenuItem { Header = "Show All Columns", IsEnabled = hidden.Count > 0 };
        showAll.Click += OnShowAllColumnsClicked;
        items.Add(showAll);
        var unfreeze = new MenuItem { Header = "Unfreeze Columns", IsEnabled = vm.FrozenColumnCount > 0 };
        unfreeze.Click += OnUnfreezeColumnsClicked;
        items.Add(unfreeze);

        flyout.ItemsSource = items;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
    }

    private void AttachRowHandlers(IEnumerable<EditableRow> rows)
    {
        foreach (var row in rows)
            row.PropertyChanged += OnRowPropertyChanged;
    }

    private void DetachRowHandlers(IEnumerable<EditableRow> rows)
    {
        foreach (var row in rows)
            row.PropertyChanged -= OnRowPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableDataTabViewModel.SelectedRow))
        {
            var grid = this.FindControl<DataGrid>("DataGrid");
            if (grid != null && !ReferenceEquals(grid.SelectedItem, _vm?.SelectedRow))
                grid.SelectedItem = _vm?.SelectedRow;
        }
    }

    private void RebuildColumns(TableDataTabViewModel vm)
    {
        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid == null)
            return;

        // A layout change rebuilds every column; keep the widths the user dragged.
        var widths = grid.Columns
            .Skip(1) // the State column, whose header could clash with a data column named State
            .Where(c => c.Header is string && c.Width.IsAbsolute)
            .ToDictionary(c => (string)c.Header!, c => c.Width);
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "State",
            IsReadOnly = true,
            CanUserResize = false,
            CanUserReorder = false,
            Width = new DataGridLength(80),
            Binding = new Avalonia.Data.Binding(nameof(EditableRow.StateDisplay))
        });

        // RAIN-P2-14 — Only the layout's visible columns, in its order; freezing any data column
        // also freezes the State column in front of it.
        grid.FrozenColumnCount = vm.FrozenColumnCount > 0 ? vm.FrozenColumnCount + 1 : 0;

        foreach (var col in vm.DisplayedColumns)
        {
            var colName = col;
            var columnType = vm.GetColumnType(colName);
            var editorKind = vm.GetEditorKind(colName);
            var column = new DataGridTemplateColumn
            {
                Header = colName,
                CanUserResize = true,
                CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EditableRow>(
                    (row, _) => TableCellControls.BuildTableCell(row, colName), supportsRecycling: false),
                CellEditingTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EditableRow>(
                    (row, _) => TableCellControls.BuildTableCellEditor(vm, row, colName, editorKind, columnType), supportsRecycling: false),
            };
            if (widths.TryGetValue(colName, out var width))
                column.Width = width;
            grid.Columns.Add(column);
        }
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        _editSnapshot = e.Row.DataContext is EditableRow row && e.Column.Header is string column
            ? (row, column, row.Values.GetValueOrDefault(column))
            : null;

    // revert-grid-fixups — Runs before CellEditEnded marks the row modified: a commit runs a
    // deferred-write editor's write-back (see BuildTextEditor); a cancel puts back the value the
    // cell had when its editor opened, undoing whatever an immediate-write editor already wrote.
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            e.EditingElement?.GetValue(TableCellControls.CommitActionProperty)?.Invoke();
        else if (_editSnapshot is var (row, column, value))
            row.Values[column] = value;
    }

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        _editSnapshot = null;

        if (_vm == null || e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not EditableRow row)
            return;

        _vm.MarkRowModified(row, e.Column?.Header as string);
        ApplyRowTag(e.Row, row.State);
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || sender is not DataGrid grid)
            return;

        _vm.SelectedRow = grid.SelectedItem as EditableRow;
    }

    private void OnDeleteRowClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedRow == null)
            return;

        _vm.DeleteRowCommand.Execute(_vm.SelectedRow);
        var grid = this.FindControl<DataGrid>("DataGrid");
        grid?.InvalidateArrange();
    }

    private async void OnCommitPreviewRequested(CommitPreviewRequest preview)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null)
            return;

        var dialog = new CommitPreviewDialog(preview);
        var confirmed = await dialog.ShowDialog<bool?>(window);

        if (confirmed == true && _vm != null)
            await _vm.ExecuteCommitAsync();
    }

    // RAIN-P2-12 — A commit found a row changed underneath the user.
    private async void OnConcurrencyConflictRequested(ConcurrencyConflictRequest conflict)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        var vm = _vm;
        var choice = await new ConcurrencyConflictDialog(conflict).ShowDialog<ConcurrencyConflictChoice?>(window);
        if (vm == null || vm != _vm)
            return;

        if (choice == ConcurrencyConflictChoice.ReloadRow)
            await vm.ReloadConflictedRowAsync(conflict.Row);
        else if (choice == ConcurrencyConflictChoice.Overwrite)
            await vm.OverwriteConflictedRowAsync(conflict.Row);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditableRow.State))
            return;

        // Row colours come from DataGridRow.Tag; re-sync it so a revert or delete shows immediately.
        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid == null)
            return;

        if (sender is EditableRow row)
        {
            foreach (var gridRow in grid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (ReferenceEquals(gridRow.DataContext, row))
                    ApplyRowTag(gridRow, row.State);
            }
        }
        grid.InvalidateArrange();
    }

    private async void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (e.Column?.Header is string columnName && _vm != null)
            await _vm.SortByColumn(columnName, additive: _lastHeaderClickModifiers.HasFlag(KeyModifiers.Shift));
    }

    private KeyModifiers _lastHeaderClickModifiers;

    // Tunnels ahead of CellPointerPressed, so a press outside any cell (row header, empty area)
    // leaves no stale clicked cell behind.
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastHeaderClickModifiers = e.KeyModifiers;
        _clickedCell = null;
    }

    private async void OnCopyAsInsertClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        var grid = this.FindControl<DataGrid>("DataGrid");
        var row = (grid?.SelectedItem as EditableRow) ?? _vm.SelectedRow;
        if (row == null)
            return;

        var sql = _vm.BuildInsertSql(row);
        await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(sql);
    }

    private async void OnCopyRowsClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid == null)
            return;

        var selectedRows = GetSelectedRows(grid);
        var text = _vm.BuildCopyRowsText(selectedRows);
        if (!string.IsNullOrWhiteSpace(text))
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
    }

    private async void OnPasteRowsClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
            _vm.PasteRowsFromText(text);
    }

    private void OnDuplicateRowClicked(object? sender, RoutedEventArgs e)
    {
        _vm?.DuplicateSelectedRowCommand.Execute(null);
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null)
            return;

        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            OnCopyRowsClicked(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            OnPasteRowsClicked(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.D && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            OnDuplicateRowClicked(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.CanDeleteSelectedRow && _vm.SelectedRow != null)
        {
            _vm.DeleteRowCommand.Execute(_vm.SelectedRow);
            e.Handled = true;
        }

        await Task.CompletedTask;
    }

    private static List<EditableRow> GetSelectedRows(DataGrid grid)
    {
        if (grid.SelectedItems?.Count > 0)
            return grid.SelectedItems.OfType<EditableRow>().ToList();

        return grid.SelectedItem is EditableRow row ? [row] : [];
    }

    private static void ApplyRowTag(DataGridRow row, EditState state)
    {
        row.Tag = state.ToString();
    }

    private void OnGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e) =>
        _clickedCell = e.Row?.DataContext is EditableRow row && e.Column?.Header is string column
            ? (row, column)
            : null;

    // The cell the single-cell/row menu actions target: the pressed cell while its row is still
    // selected, otherwise (menu opened from the keyboard, a row header, or after the selection moved)
    // the selected row's current cell.
    private (EditableRow Row, string Column)? MenuTargetCell()
    {
        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid == null)
            return null;
        if (_clickedCell is var (clickedRow, _) && grid.SelectedItems.Contains(clickedRow))
            return _clickedCell;
        return grid.SelectedItem is EditableRow row && grid.CurrentColumn?.Header is string column
            ? (row, column)
            : null;
    }

    private async void OnGridContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var target = MenuTargetCell();
        var row = target?.Row;
        var colName = target?.Column;

        if (this.FindControl<MenuItem>("HideColumnMenuItem") is { } hideColumnItem)
            hideColumnItem.IsEnabled = colName != null && _vm?.DisplayedColumns.Count > 1;
        if (this.FindControl<MenuItem>("FreezeColumnsMenuItem") is { } freezeColumnsItem)
            freezeColumnsItem.IsEnabled = colName != null;
        if (this.FindControl<MenuItem>("UnfreezeColumnsMenuItem") is { } unfreezeColumnsItem)
            unfreezeColumnsItem.IsEnabled = _vm?.FrozenColumnCount > 0;

        foreach (var name in new[] { "ReferencedRowMenuItem", "ReferencingRowsMenuItem", "ForeignKeyLookupMenuItem" })
            if (this.FindControl<MenuItem>(name) is { } menu) { menu.Items.Clear(); menu.IsEnabled = false; }
        var vm = _vm;
        if (vm != null)
        {
            try
            {
                await vm.LoadForeignKeysAsync();
                if (ReferenceEquals(vm, _vm) && MenuTargetCell() == target)
                    PopulateForeignKeyMenus(row, colName);
            }
            catch (Exception ex) { vm.StatusMessage = ex.Message; }
        }

        var menuValue = row != null && colName != null ? row.Values.GetValueOrDefault(colName) : null;
        ViewBinaryMenuItem.IsVisible = menuValue is byte[];
        ViewValueMenuItem.IsVisible = CellValueViewerViewModel.TextOf(menuValue) != null;
        if (this.FindControl<MenuItem>("RevertCellMenuItem") is { } revertCellItem)
            revertCellItem.IsEnabled = _vm?.CanRevertCell(row, colName) == true;
        if (this.FindControl<MenuItem>("RevertRowMenuItem") is { } revertRowItem)
            revertRowItem.IsEnabled = _vm?.CanRevertRow(row) == true;
    }

    private void PopulateForeignKeyMenus(EditableRow? row, string? column)
    {
        if (_vm == null) return;
        var outgoing = _vm.ForeignKeys.Where(fk => _vm.MatchesTable(fk.Table) && fk.Columns.Contains(column ?? "")).ToList();
        var incoming = _vm.ForeignKeys.Where(fk => _vm.MatchesTable(fk.ReferencedTable)).ToList();
        Populate("ReferencedRowMenuItem", outgoing, fk => OpenRelatedRow(row!, fk, false), row != null);
        Populate("ReferencingRowsMenuItem", incoming, fk => OpenRelatedRow(row!, fk, true), row != null);
        Populate("ForeignKeyLookupMenuItem", outgoing, fk => ShowForeignKeyLookup(row!, fk),
            row != null && !_vm.IsCommitting && row.State != EditState.Deleted
            && (row.State == EditState.Inserted || _vm.CanEditExistingRows));

        void Populate(string name, IReadOnlyList<ForeignKeyRelationship> relationships, Action<ForeignKeyRelationship> action, bool enabled)
        {
            if (this.FindControl<MenuItem>(name) is not { } menu) return;
            menu.Items.Clear();
            menu.IsEnabled = enabled && relationships.Count > 0;
            foreach (var fk in relationships)
            {
                var item = new MenuItem { Header = $"{fk.Name}: {fk.Table} → {fk.ReferencedTable}" };
                item.Click += (_, _) => action(fk);
                menu.Items.Add(item);
            }
        }
    }

    private void OpenRelatedRow(EditableRow row, ForeignKeyRelationship relationship, bool referencing)
    {
        if (_vm?.CreateRelatedTab(row, relationship, referencing) is not { } tab) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
        {
            main.Tabs.Add(tab);
            main.SelectedTab = tab;
        }
    }

    private async void ShowForeignKeyLookup(EditableRow row, ForeignKeyRelationship relationship)
    {
        if (_vm == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        this.FindControl<DataGrid>("DataGrid")?.CancelEdit();
        var vm = _vm;
        var lookup = new ForeignKeyLookupViewModel(vm, relationship);
        var value = await new ForeignKeyLookupDialog(lookup).ShowDialog<ForeignKeyLookupValue?>(owner);
        if (value != null) vm.ApplyForeignKeyValue(row, relationship, value);
    }

    private void OnRevertCellClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && MenuTargetCell() is (var row, var colName))
            _vm.RevertCell(row, colName);
    }

    private void OnRevertRowClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && MenuTargetCell() is (var row, _))
            _vm.RevertRowCommand.Execute(row);
    }

    // revert-grid-fixups — An editor left open across a revert would write its value on top of the
    // reverted row, so it's cancelled (restoring its cell, see OnCellEditEnding) before the revert runs.
    private void OnRevertStarting() => this.FindControl<DataGrid>("DataGrid")?.CancelEdit();

    // revert-grid-fixups — Every revert path (cell, row, Revert All, a rejected edit) lands here.
    private void OnRowValuesReverted(IReadOnlyList<EditableRow> rows)
    {
        var grid = this.FindControl<DataGrid>("DataGrid");
        if (grid == null)
            return;

        foreach (var row in rows)
            RefreshRowCells(grid, row);
    }

    // RAIN-P2-11 — Cells are built once from the row's Values dictionary rather than bound, so a
    // reverted value only shows once the cell's content is rebuilt.
    private static void RefreshRowCells(DataGrid grid, EditableRow row)
    {
        foreach (var column in grid.Columns.OfType<DataGridTemplateColumn>())
        {
            if (column.Header is string colName && column.GetCellContent(row)?.Parent is DataGridCell cell)
                cell.Content = TableCellControls.BuildTableCell(row, colName);
        }
    }

    // RAIN-P2-18 — Binary cells open the blob editor; any other non-NULL value opens the read-only value viewer.
    private async void OnViewValueClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || MenuTargetCell() is not (var row, var colName) || TopLevel.GetTopLevel(this) is not Window window)
            return;

        if (row.Values.GetValueOrDefault(colName) is not byte[] bytes)
        {
            await CellValueViewerWindow.ShowAsync(window, row.Values.GetValueOrDefault(colName), colName);
            return;
        }
        var allowEdit = _vm.CanEditExistingRows || row.State == EditState.Inserted;
        var dialog = new BlobEditorDialog(bytes, colName, allowEdit);
        var result = await dialog.ShowDialog<BlobEditResult?>(window);

        if (result == null) return;

        row.Values[colName] = result.Action switch
        {
            BlobEditAction.SetNull => null,
            BlobEditAction.SetEmpty => Array.Empty<byte>(),
            _ => result.Bytes
        };
        _vm.MarkRowModified(row, colName);
    }
}
