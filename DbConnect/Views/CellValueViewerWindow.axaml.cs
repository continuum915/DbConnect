using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// RAIN-P2-18 — Read-only viewer for one cell's value: the JSON tree and the long-text view of
/// <see cref="CellValueViewerViewModel"/>. The editor has no search panel; highlighting and folding
/// are installed only while the view model allows them.
/// </summary>
public partial class CellValueViewerWindow : Window
{
    private readonly CellValueViewerViewModel? _viewModel;
    private EditorThemeBinding? _highlighting;
    private FoldingManager? _foldings;
    private int _textVersion;

    public CellValueViewerWindow() { InitializeComponent(); } // designer

    public CellValueViewerWindow(CellValueViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Editor.TemplateApplied += (_, _) => Editor.SearchPanel?.Uninstall(); // the viewer has no search
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Opened += async (_, _) => await viewModel.LoadAsync();
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _textVersion++; // a document still being built is dropped
            SetHighlighting(false);
            viewModel.Dispose();
        };
        ShowText();
    }

    public CellValueViewerViewModel? ViewModel => _viewModel;

    /// <summary>Opens the viewer for a cell value; false when the value is NULL or binary, which this viewer doesn't show.</summary>
    public static async Task<bool> ShowAsync(Window owner, object? value, string columnName)
    {
        if (CellValueViewerViewModel.TextOf(value) is not { } text)
            return false;

        await new CellValueViewerWindow(new CellValueViewerViewModel(text, columnName)).ShowDialog(owner);
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CellValueViewerViewModel.DisplayText) or nameof(CellValueViewerViewModel.IsHighlightingEnabled))
            ShowText();
    }

    /// <summary>
    /// Shows the view model's text. Beyond the full tier the document is built off the UI thread,
    /// so only the editor's own setup of the new document runs on it.
    /// </summary>
    private async void ShowText()
    {
        if (_viewModel == null || _viewModel.IsParsing)
            return;

        var version = ++_textVersion;
        var text = _viewModel.DisplayText ?? string.Empty;
        SetHighlighting(false);
        if (text.Length > CellViewerLimits.FullMaxChars)
        {
            var document = await Task.Run(() => DetachedDocument(text));
            if (version != _textVersion)
                return;
            document.SetOwnerThread(Thread.CurrentThread);
            Editor.Document = document;
        }
        else
        {
            Editor.Text = text;
        }

        SetHighlighting(_viewModel.IsHighlightingEnabled);
    }

    /// <summary>A document built on this thread and released, so the UI thread can take it over.</summary>
    private static TextDocument DetachedDocument(string text)
    {
        var document = new TextDocument(text);
        document.SetOwnerThread(null);
        return document;
    }

    private void SetHighlighting(bool enabled)
    {
        if (!enabled)
        {
            _highlighting?.Dispose();
            _highlighting = null;
            if (_foldings != null)
                FoldingManager.Uninstall(_foldings);
            _foldings = null;
            return;
        }

        _highlighting = EditorThemeBinding.TryAttach(Editor, ".json");

        _foldings = FoldingManager.Install(Editor.TextArea);
        _foldings.UpdateFoldings(JsonFoldingRanges.Compute(Editor.Text)
            .OrderBy(range => range.Start)
            .Select(range => new NewFolding(range.Start, range.End + 1)), -1);
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null && Clipboard != null)
            await Clipboard.SetTextAsync(_viewModel.Value);
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        var extension = System.IO.Path.GetExtension(_viewModel.SuggestedFileName).TrimStart('.');
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Value to File",
            SuggestedFileName = _viewModel.SuggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] },
                FilePickerFileTypes.All,
            ],
        });
        if (file == null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0); // overwriting a longer file mustn't leave its tail behind
            await _viewModel.SaveToAsync(stream);
        }
        catch (Exception ex)
        {
            await new ConfirmDialog("Save failed", ex.Message, "OK", "Close").ShowDialog<bool>(this);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
