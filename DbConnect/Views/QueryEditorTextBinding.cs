using System;
using System.ComponentModel;
using AvaloniaEdit;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Publishes immutable document revisions and suppresses editor-origin binding round trips.</summary>
internal sealed class QueryEditorTextBinding
{
    private readonly TextEditor _editor;
    private readonly QueryTextUndoCache _undoCache = new();
    private QueryTabViewModel? _viewModel;
    private QueryTabViewModel? _publishingModel;
    private long _publishingRevision;
    private TextDocument? _document;
    private bool _applyingModel;
    private int _changes;
    private bool _singleChangeDiffers;
    private long _documentRevision;
    private long _publishedDocumentRevision = -1;
    private long _publishedModelRevision = -1;

    public QueryEditorTextBinding(TextEditor editor)
    {
        _editor = editor;
        QueryEditorWatermarkBehavior.Attach(editor);
        editor.TextChanged += OnTextChanged;
        editor.DocumentChanged += (_, _) => WatchDocument();
        WatchDocument();
    }

    public string ReadCurrentText() => _viewModel is { } model &&
        _documentRevision == _publishedDocumentRevision && model.QueryTextRevision == _publishedModelRevision
        ? model.QueryText : _editor.Text;

    public void Bind(QueryTabViewModel? model)
    {
        if (ReferenceEquals(_viewModel, model)) return;
        _undoCache.Clear();
        if (_viewModel != null) _viewModel.PropertyChanged -= OnModelChanged;
        _viewModel = model;
        if (model == null) return;
        ApplyModelText(model.QueryText);
        model.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueryTabViewModel.QueryText) || sender is not QueryTabViewModel model ||
            !ReferenceEquals(model, _viewModel)) return;
        if (ReferenceEquals(model, _publishingModel) || _applyingModel)
        {
            if (ReferenceEquals(model, _publishingModel) && model.QueryTextRevision == _publishingRevision) return;
            // TextEditor's setter clears undo history, which requires the active edit group to finish.
            var revision = model.QueryTextRevision;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(model, _viewModel) && model.QueryTextRevision == revision)
                    ApplyModelText(model.QueryText);
            });
            return;
        }
        ApplyModelText(model.QueryText);
    }

    private void ApplyModelText(string text)
    {
        _undoCache.Clear();
        var model = _viewModel;
        var revision = model?.QueryTextRevision;
        // Replacing the text moves the caret to the start; keep its line and column where they still exist
        // (for example when a file reloads after changing on disk).
        var caret = _editor.TextArea.Caret.Location;
        _applyingModel = true;
        try { _editor.Text = text; }
        finally { _applyingModel = false; }
        if (caret.Line > 1 || caret.Column > 1)
        {
            var line = Math.Min(caret.Line, _editor.Document.LineCount);
            var column = Math.Min(caret.Column, _editor.Document.GetLineByNumber(line).Length + 1);
            _editor.TextArea.Caret.Location = new TextLocation(line, column);
        }
        if (model != null && ReferenceEquals(model, _viewModel) && model.QueryTextRevision == revision)
        {
            _publishedDocumentRevision = _documentRevision;
            _publishedModelRevision = model.QueryTextRevision;
        }
    }

    private void WatchDocument()
    {
        _undoCache.Clear();
        _documentRevision++;
        if (_document != null) _document.Changing -= OnChanging;
        _document = _editor.Document;
        if (_document != null) _document.Changing += OnChanging;
        _changes = 0;
        _singleChangeDiffers = false;
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        var coherent = _documentRevision == _publishedDocumentRevision &&
            _viewModel?.QueryTextRevision == _publishedModelRevision;
        _documentRevision++;
        _changes++;
        if (coherent && !_applyingModel && _changes == 1 && _viewModel is { } model &&
            e.RemovalLength <= 256 && e.InsertionLength <= 256)
            _undoCache.Prepare(e.Offset, e.RemovedText.Text, e.InsertedText.Text,
                model.CurrentQueryTextSnapshot);
        else _undoCache.Clear();
        _singleChangeDiffers = e.RemovalLength != e.InsertionLength ||
            e.RemovalLength <= 256 && !string.Equals(e.RemovedText.Text, e.InsertedText.Text, StringComparison.Ordinal);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        using var diagnostic = EditorSetupDiagnostics.Begin("publish-editor-snapshot");
        if (!ReferenceEquals(_document, _editor.Document)) WatchDocument();
        var knownDifferent = _changes == 1 && _singleChangeDiffers;
        var restored = _changes == 1 ? _undoCache.Complete() : null;
        if (_changes != 1) _undoCache.Clear();
        _changes = 0;
        if (_viewModel is not { } model) return;
        if (!_applyingModel)
        {
            var source = _document?.CreateSnapshot();
            var snapshot = restored ?? (source == null ? QueryTextSnapshot.FromString(string.Empty)
                : new QueryTextSnapshot(source.TextLength, () => source.Text, source.CreateReader));
            var previousModel = _publishingModel;
            var previousRevision = _publishingRevision;
            _publishingModel = model;
            _publishingRevision = model.QueryTextRevision + 1;
            var documentRevision = _documentRevision;
            var expectedRevision = _publishingRevision;
            try
            {
                model.PublishEditorQueryText(snapshot, knownDifferent);
                if (ReferenceEquals(model, _viewModel) && _documentRevision == documentRevision && model.QueryTextRevision <= expectedRevision)
                {
                    _publishedDocumentRevision = documentRevision;
                    _publishedModelRevision = model.QueryTextRevision;
                }
            }
            finally { _publishingModel = previousModel; _publishingRevision = previousRevision; }
        }
        model.ErrorSpan = null;
        if (model.IsExecuting) model.RunningStatementSpan = null;
    }
}
