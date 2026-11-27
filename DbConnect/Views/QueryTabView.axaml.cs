using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using AvaloniaEdit.CodeCompletion;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class QueryTabView : UserControl
{
    private CompletionWindow? _completionWindow;
    private QueryTabViewModel? _boundViewModel;
    private readonly RunningStatementRenderer _runningStatementRenderer = new();
    private readonly QueryErrorUnderlineRenderer _errorUnderlineRenderer = new();
    private readonly SqlEditorStructureBehavior? _editorStructure;
    private readonly QueryEditorTextBinding? _editorText;
    /// <summary>Maps each generated results-grid column back to its data ordinal, independent of visual reordering.</summary>
    private readonly Dictionary<DataGridColumn, int> _resultsColumnOrdinals = new();

    public QueryTabView()
    {
        InitializeComponent();

        if (QueryEditor == null)
            return;

        _editorText = new QueryEditorTextBinding(QueryEditor);

        QueryEditor.TextArea.SelectionChanged += (_, _) =>
        {
            if (DataContext is QueryTabViewModel vm)
            {
                vm.SelectedQueryText = QueryEditor.SelectedText;
                vm.SelectedQueryStart = QueryEditor.SelectionStart;
            }
        };

        QueryEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (DataContext is QueryTabViewModel vm)
                vm.CaretOffset = QueryEditor.CaretOffset;
        };

        QueryEditor.TextArea.TextEntering += TextArea_TextEntering;
        QueryEditor.TextArea.TextEntered += TextArea_TextEntered;
        QueryEditor.TextArea.TextView.BackgroundRenderers.Add(_runningStatementRenderer);
        QueryEditor.TextArea.TextView.BackgroundRenderers.Add(_errorUnderlineRenderer);
        QueryEditor.KeyDown += QueryEditor_KeyDown;
        EditorSearch.Attach(QueryEditor);
        EditorPreferencesBinding.Attach(QueryEditor);
        _editorStructure = new SqlEditorStructureBehavior(QueryEditor,
            () => (DataContext as QueryTabViewModel)?.SelectedSession?.Capabilities.BatchDialect ?? SqlBatchDialect.Standard, _editorText.ReadCurrentText);
        if (ResultsGrid != null)
        {
            ResultsGrid.KeyDown += ResultsGrid_KeyDown;
            ResultGridScrollPaging.Attach(ResultsGrid, () => (DataContext as QueryTabViewModel)?.Paging);
            ResultsGrid.CellEditEnding += ResultsGrid_CellEditEnding;
        }

        LargeDocumentMode.Attach(QueryEditor, ".sql");
        EditorCaretStatus.Attach(QueryEditor);
    }

    public void OpenSearch() => EditorSearch.Open();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_boundViewModel != null)
        {
            _boundViewModel.InsertTextRequested -= OnInsertTextRequested;
            _boundViewModel.ResultEditor.PropertyChanged -= OnResultEditorPropertyChanged;
            _boundViewModel.ResultEditor.RowsRefreshed -= OnResultEditorRowsRefreshed;
            _boundViewModel.ResultEditor.CommitPreviewRequested -= OnResultCommitPreviewRequested;
            _boundViewModel.ResultEditor.ConcurrencyConflictRequested -= OnResultConflictRequested;
            _boundViewModel.ResultGrid.ColumnVisibilityChanged -= ApplyResultColumnVisibility;
            _boundViewModel.PropertyChanged -= OnBoundViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);

        _boundViewModel = DataContext as QueryTabViewModel;
        _editorText?.Bind(_boundViewModel);
        _editorStructure?.Refresh();
        if (_boundViewModel is not { } vm)
        {
            UpdateRunningStatement(null);
            UpdateErrorSpan(null);
            return;
        }

        _boundViewModel.InsertTextRequested += OnInsertTextRequested;
        _boundViewModel.ResultEditor.PropertyChanged += OnResultEditorPropertyChanged;
        _boundViewModel.ResultEditor.RowsRefreshed += OnResultEditorRowsRefreshed;
        _boundViewModel.ResultEditor.CommitPreviewRequested += OnResultCommitPreviewRequested;
        _boundViewModel.ResultEditor.ConcurrencyConflictRequested += OnResultConflictRequested;
        _boundViewModel.ResultGrid.ColumnVisibilityChanged += ApplyResultColumnVisibility;
        _boundViewModel.ResultEditor.DiscardConfirmation = ConfirmDiscardingResultEditsAsync;
        _boundViewModel.ConfirmFullQueryExport = async disclosure => TopLevel.GetTopLevel(this) is Window window &&
            await new ConfirmDialog("Rerun full query for export?", disclosure, "Rerun and export", "Cancel").ShowDialog<bool>(window);

        vm.PropertyChanged += OnBoundViewModelPropertyChanged;
        UpdateRunningStatement(vm.RunningStatementSpan);
        UpdateErrorSpan(vm.ErrorSpan);
        UpdateResultsGrid(vm.CurrentResult);
        UpdateExplainGrid(vm.ExplainResult);
    }

    private void OnBoundViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not QueryTabViewModel vm)
            return;

        if (args.PropertyName == nameof(QueryTabViewModel.CurrentResult))
            UpdateResultsGrid(vm.CurrentResult);
        else if (args.PropertyName == nameof(QueryTabViewModel.DisplayResultRows))
            UpdateResultsGrid(vm.CurrentResult);
        else if (args.PropertyName == nameof(QueryTabViewModel.ExplainResult))
            UpdateExplainGrid(vm.ExplainResult);
        else if (args.PropertyName == nameof(QueryTabViewModel.RunningStatementSpan))
            UpdateRunningStatement(vm.RunningStatementSpan);
        else if (args.PropertyName == nameof(QueryTabViewModel.ErrorSpan))
            UpdateErrorSpan(vm.ErrorSpan);
        else if (args.PropertyName == nameof(QueryTabViewModel.SelectedSession))
            _editorStructure?.Refresh();
    }

    private void UpdateRunningStatement(StatementSpan? span)
    {
        _runningStatementRenderer.Span = span;
        QueryEditor?.TextArea.TextView.InvalidateLayer(_runningStatementRenderer.Layer);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is QueryTabViewModel vm)
        {
            vm.ExportPathPicker = new StorageProviderExportPathPicker(TopLevel.GetTopLevel(this));
            vm.CaptureExportColumnOrder = () => ResultsGrid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex)
                .Select(c => _resultsColumnOrdinals[c]).ToArray();
        }
    }

    private void QueryEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
            return;
        }

        var windowVm = TopLevel.GetTopLevel(this) is Window window
            ? window.DataContext as MainWindowViewModel
            : null;

        if (windowVm == null)
            return;

        if (e.Key == Key.F5)
        {
            windowVm.ExecuteQueryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            windowVm.ExecuteSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (DataContext is QueryTabViewModel vm)
                vm.FormatSqlCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            windowVm.ExplainQueryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F8)
        {
            windowVm.CancelQueryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            windowVm.NewQueryTabCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
        {
            windowVm.CloseSelectedTabCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ResultsGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QueryTabViewModel vm)
            return;

        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            await CopyCellAsync(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            await CopyAllAsync(vm);
            e.Handled = true;
        }
    }

    private void OnInsertTextRequested(string text)
    {
        if (QueryEditor == null)
            return;

        var startOffset = QueryEditor.SelectionLength > 0
            ? QueryEditor.SelectionStart
            : QueryEditor.CaretOffset;
        var replaceLength = QueryEditor.SelectionLength;

        QueryEditor.Document.Replace(startOffset, replaceLength, text);
        QueryEditor.CaretOffset = startOffset + text.Length;
        QueryEditor.Focus();
    }

    private void UpdateExplainGrid(QueryResult? result)
    {
        if (ExplainGrid == null) return;

        ExplainGrid.Columns.Clear();
        ExplainGrid.ItemsSource = null;

        if (result == null || result.HasError)
            return;

        for (var i = 0; i < result.ColumnInfos.Count; i++)
        {
            ExplainGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnInfos[i].Name,
                Binding = new Avalonia.Data.Binding($"[{i}]")
            });
        }

        ExplainGrid.ItemsSource = QueryResultFormattingService
            .BuildDisplayRows(result)
            .Select(row => row.DisplayValues)
            .ToList();
    }

    private void UpdateResultsGrid(QueryResult? result)
    {
        if (ResultsGrid == null) return;

        ResultsGrid.Columns.Clear();
        ResultsGrid.ItemsSource = null;
        _resultsColumnOrdinals.Clear();

        var editor = (DataContext as QueryTabViewModel)?.ResultEditor;
        ResultsGrid.IsReadOnly = editor?.IsEditable != true;

        if (result == null || result.HasError || !result.HasResultSet)
            return;

        for (var i = 0; i < result.ColumnInfos.Count; i++)
        {
            var column = editor?.IsEditable == true
                ? EditableResultColumns.Build(editor, result.ColumnInfos[i].Name, i)
                : EditableResultColumns.BuildReadOnly(result.ColumnInfos[i].Name, i);
            ResultsGrid.Columns.Add(column);
            _resultsColumnOrdinals[column] = i;
        }

        ApplyResultColumnVisibility();
        if (DataContext is QueryTabViewModel vm)
            ResultsGrid.ItemsSource = vm.DisplayResultRows;
    }

    // RAIN-P2-17 — The column chooser's state, applied to the generated columns by ordinal.
    private void ApplyResultColumnVisibility()
    {
        if (DataContext is not QueryTabViewModel vm)
            return;

        var chooser = vm.ResultGrid.Columns;
        foreach (var (column, ordinal) in _resultsColumnOrdinals)
            column.IsVisible = ordinal >= chooser.Count || chooser[ordinal].IsVisible;
    }

    // RAIN-P2-16 — An editable result's cell editor (EditableResultColumns) writes its text back
    // here, when the grid commits the edit; text that doesn't parse keeps the editor open.
    private void ResultsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || DataContext is not QueryTabViewModel vm ||
            e.EditingElement is not TextBox box || !_resultsColumnOrdinals.TryGetValue(e.Column, out var ordinal) ||
            vm.ResultEditor.RowFor(e.Row.DataContext as IReadOnlyList<string>) is not { } row)
            return;

        if (vm.ResultEditor.SetCellText(row, ordinal, box.Text) != null)
            e.Cancel = true;
    }

    private void SetCellNull_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QueryTabViewModel vm || ResultsGrid.CurrentColumn is not { } column ||
            !_resultsColumnOrdinals.TryGetValue(column, out var ordinal) ||
            vm.ResultEditor.RowFor(ResultsGrid.SelectedItem as IReadOnlyList<string>) is not { } row)
            return;

        vm.ResultEditor.SetCellNull(row, ordinal);
        EditableResultColumns.RefreshCells(ResultsGrid, vm.ResultEditor, _resultsColumnOrdinals);
    }

    private void OnResultEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryResultEditorViewModel.IsEditable) && DataContext is QueryTabViewModel vm)
            UpdateResultsGrid(vm.CurrentResult);
    }

    private void OnResultEditorRowsRefreshed()
    {
        if (DataContext is QueryTabViewModel vm)
            EditableResultColumns.RefreshCells(ResultsGrid, vm.ResultEditor, _resultsColumnOrdinals);
    }

    private async void OnResultCommitPreviewRequested(CommitPreviewRequest preview)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || DataContext is not QueryTabViewModel vm)
            return;

        var editor = vm.ResultEditor;
        if (await new CommitPreviewDialog(preview).ShowDialog<bool?>(window) == true)
            await editor.ExecuteCommitAsync();
    }

    // RAIN-P2-12's dialog, as in the table tab: a result commit found a row changed underneath the user.
    private async void OnResultConflictRequested(ConcurrencyConflictRequest conflict)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || DataContext is not QueryTabViewModel vm)
            return;

        var editor = vm.ResultEditor;
        var choice = await new ConcurrencyConflictDialog(conflict).ShowDialog<ConcurrencyConflictChoice?>(window);
        if (choice == ConcurrencyConflictChoice.ReloadRow)
            await editor.ReloadConflictedRowAsync(conflict.Row);
        else if (choice == ConcurrencyConflictChoice.Overwrite)
            await editor.OverwriteConflictedRowAsync(conflict.Row);
    }

    private async Task<bool> ConfirmDiscardingResultEditsAsync(int editedRows)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return false;

        var message = $"{editedRows} edited row(s) in the result haven't been committed. Continuing discards those edits.";
        return await new ConfirmDialog("Uncommitted result edits", message, "Discard edits", "Keep editing").ShowDialog<bool>(window);
    }

    private async void CopyCell_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryTabViewModel vm)
            await CopyCellAsync(vm);
    }

    // RAIN-P2-18 — The read-only value viewer for the current cell; NULL and binary cells have no text to show.
    private void ResultsMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e) =>
        ViewValueMenuItem.IsEnabled = CellValueViewerViewModel.TextOf(CurrentResultCell()?.Value) != null;

    private async void ViewValue_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window && CurrentResultCell() is (var value, var columnName))
            await CellValueViewerWindow.ShowAsync(window, value, columnName);
    }

    private (object? Value, string ColumnName)? CurrentResultCell() =>
        DataContext is QueryTabViewModel vm && ResultsGrid.CurrentColumn is { } column &&
        _resultsColumnOrdinals.TryGetValue(column, out var ordinal) && ResultsGrid.SelectedItem is IReadOnlyList<string> row
            ? (vm.ResultGrid.ValueAt(row, ordinal), column.Header as string ?? "value")
            : null;

    private async void CopySelectedRows_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryTabViewModel vm)
            await CopySelectedRowsAsync(vm);
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryTabViewModel vm)
            await CopyAllAsync(vm);
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryTabViewModel vm)
            await ExportCsvAsync(vm);
    }

    private async Task CopyCellAsync(QueryTabViewModel vm)
    {
        var row = ResultsGrid.SelectedItem as IReadOnlyList<string>;
        var ordinal = ResultsGrid.CurrentColumn != null && _resultsColumnOrdinals.TryGetValue(ResultsGrid.CurrentColumn, out var found)
            ? found
            : -1;
        var text = vm.ResultGrid.BuildCellText(row, ordinal);
        if (!string.IsNullOrWhiteSpace(text))
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
    }

    /// <summary>Copies the selected rows in their displayed order, not the order they were selected in.</summary>
    private async Task CopySelectedRowsAsync(QueryTabViewModel vm)
    {
        var selected = ResultsGrid.SelectedItems.OfType<IReadOnlyList<string>>().ToHashSet(ReferenceEqualityComparer.Instance);
        if (selected.Count == 0)
            return;

        var text = vm.ResultGrid.BuildRowsText(vm.DisplayResultRows.Where(selected.Contains));
        if (!string.IsNullOrWhiteSpace(text))
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
    }

    private async Task CopyAllAsync(QueryTabViewModel vm)
    {
        var text = vm.ResultGrid.BuildRowsText(vm.DisplayResultRows);
        if (!string.IsNullOrWhiteSpace(text))
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
    }

    private static Task ExportCsvAsync(QueryTabViewModel vm) => vm.ExportCommand.ExecuteAsync(null);

    private void TextArea_TextEntered(object? sender, TextInputEventArgs e)
    {
        if (!AppPreferences.Editor.AutoOpenCompletion
            || string.IsNullOrEmpty(e.Text) || !(char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_' || e.Text[0] == '.'))
            return;
        _ = ShowCompletionAsync();
    }

    /// <summary>Opens completion at the caret: while typing (when enabled) and on Ctrl+Space.</summary>
    internal async Task ShowCompletionAsync()
    {
        if (DataContext is not QueryTabViewModel { SelectedSession: { } session } vm || QueryEditor == null)
            return;

        if (_completionWindow != null) return;
        var text = _editorText?.ReadCurrentText() ?? QueryEditor.Text;
        if (SqlCompletionGuard.IsInsideStringOrComment(text, QueryEditor.CaretOffset))
            return;

        var caret = QueryEditor.CaretOffset;
        System.Collections.Generic.IReadOnlyList<SqlCompletionCandidate> candidates;
        try
        {
            candidates = await CatalogSqlCompletion.GetCandidatesAsync(text, caret, session);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (QueryEditor.CaretOffset != caret || (_editorText?.ReadCurrentText() ?? QueryEditor.Text) != text || _completionWindow != null ||
            vm.SelectedSession != session || session.Catalog.IsDisposed)
            return;
        if (candidates.Count == 0)
            return;

        _completionWindow = new CompletionWindow(QueryEditor.TextArea) { StartOffset = SqlCompletionEngine.WordStart(text, caret) };
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        foreach (var candidate in candidates)
            _completionWindow.CompletionList.CompletionData.Add(new SqlCompletionData(candidate.Text, candidate.Kind, insertionText: candidate.InsertionText));
        _completionWindow.Show();
    }

    private void TextArea_TextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text?.Length > 0 && _completionWindow != null)
        {
            if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
                _completionWindow.CompletionList.RequestInsertion(e);
        }
    }
}
