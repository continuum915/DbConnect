using System;
using System.Linq;
using Avalonia;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using DbConnect.Services;

namespace DbConnect.Views;

/// <summary>Owns folding and paired-parenthesis highlighting for a query editor.</summary>
internal sealed class SqlEditorStructureBehavior
{
    private readonly TextEditor _editor;
    private readonly Func<SqlBatchDialect> _dialect;
    private readonly Func<string> _readText;
    private readonly MatchingParenthesisRenderer _renderer = new();
    private FoldingManager? _foldings;
    private SqlEditorStructure? _structure;
    private SqlEditorStructureTracker? _tracker;
    private TextDocument? _document;

    public SqlEditorStructureBehavior(TextEditor editor, Func<SqlBatchDialect> dialect, Func<string>? readText = null)
    {
        _editor = editor;
        _dialect = dialect;
        _readText = readText ?? (() => editor.Text);
        editor.AttachedToVisualTree += OnAttached;
        editor.DetachedFromVisualTree += OnDetached;
    }

    public void Refresh()
    {
        using var diagnostic = EditorSetupDiagnostics.Begin("structure-refresh");
        if (_foldings == null)
            return;

        _tracker = new SqlEditorStructureTracker(_readText(), _dialect());
        _structure = _tracker.Structure;
        _foldings.UpdateFoldings(_structure.Foldings.Select(span => new NewFolding(span.Start, span.End)), -1);
        UpdateHighlight(this, EventArgs.Empty);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_editor.Document != null)
            _foldings = FoldingManager.Install(_editor.TextArea);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _editor.DocumentChanged += OnDocumentChanged;
        WatchDocument();
        _editor.TextChanged += OnTextChanged;
        _editor.TextArea.Caret.PositionChanged += UpdateHighlight;
        Refresh();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _editor.DocumentChanged -= OnDocumentChanged;
        if (_document != null) _document.Changing -= OnDocumentChanging;
        _document = null;
        _tracker = null;
        _editor.TextChanged -= OnTextChanged;
        _editor.TextArea.Caret.PositionChanged -= UpdateHighlight;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
        if (_foldings != null)
            FoldingManager.Uninstall(_foldings);
        _foldings = null;
        _structure = null;
        _renderer.Pair = null;
    }

    private void WatchDocument()
    {
        if (_document != null) _document.Changing -= OnDocumentChanging;
        _document = _editor.Document;
        if (_document != null) _document.Changing += OnDocumentChanging;
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // A folding manager belongs to one TextDocument for its entire lifetime.
        if (_foldings != null)
            FoldingManager.Uninstall(_foldings);
        _foldings = _editor.Document == null ? null : FoldingManager.Install(_editor.TextArea);
        _tracker = null;
        _structure = null;
        _renderer.Pair = null;
        WatchDocument();
        Refresh();
    }

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (e.InsertionLength > 256 || e.RemovalLength > 256)
        {
            _tracker?.InvalidateEdit();
            return;
        }
        if (_document != null)
            _tracker?.PrepareEdit(e.Offset, e.RemovalLength, e.InsertedText.Text, _document.GetText);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        using var diagnostic = EditorSetupDiagnostics.Begin("structure-edit");
        if (_tracker == null || _foldings == null) { Refresh(); return; }
        var incremental = _tracker.CompleteEdit(_readText, _dialect());
        _structure = _tracker.Structure;
        // FoldingManager already shifts existing sections for document changes.
        if (!incremental)
            _foldings.UpdateFoldings(_structure.Foldings.Select(span => new NewFolding(span.Start, span.End)), -1);
        UpdateHighlight(this, EventArgs.Empty);
    }

    private void UpdateHighlight(object? sender, EventArgs e)
    {
        _renderer.Pair = _structure?.FindMatchingParentheses(_editor.CaretOffset);
        _editor.TextArea.TextView.InvalidateLayer(_renderer.Layer);
    }
}
