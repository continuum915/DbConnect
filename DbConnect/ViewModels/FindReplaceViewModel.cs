using System;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

/// <summary>Literal find/replace on an editor document, with an anchored selection scope and grouped undo.</summary>
public sealed partial class FindReplaceViewModel : ObservableObject
{
    private readonly TextDocument _document;
    private TextAnchor? _selectionStart;
    private TextAnchor? _selectionEnd;
    private int _nextOffset;
    private int? _currentMatch;

    public FindReplaceViewModel(TextDocument document)
    {
        _document = document;
        _document.Changed += (_, _) => _currentMatch = null;
    }

    public event Action<int, int>? MatchSelected;
    public event Action? Closed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand), nameof(ReplaceCommand), nameof(ReplaceAllCommand))]
    private string _searchText = "";

    [ObservableProperty]
    private string _replacementText = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _inSelection;

    [ObservableProperty]
    private bool _canUseSelection;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(ReplaceAllCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    private string _status = "";

    public void Open(int selectionStart, int selectionLength, int caret)
    {
        if (!IsOpen)
        {
            _selectionStart = _document.CreateAnchor(selectionStart);
            _selectionStart.MovementType = AnchorMovementType.BeforeInsertion;
            _selectionStart.SurviveDeletion = true;
            _selectionEnd = _document.CreateAnchor(selectionStart + selectionLength);
            _selectionEnd.MovementType = AnchorMovementType.AfterInsertion;
            _selectionEnd.SurviveDeletion = true;
            CanUseSelection = selectionLength > 0;
            InSelection = false;
            MoveCaret(caret);
            Status = "";
        }
        IsOpen = true;
    }

    public void MoveCaret(int offset)
    {
        _nextOffset = offset;
        _currentMatch = null;
    }

    partial void OnSearchTextChanged(string value) => ResetSearch();
    partial void OnMatchCaseChanged(bool value) => ResetSearch();
    partial void OnInSelectionChanged(bool value) => ResetSearch();

    private void ResetSearch()
    {
        _currentMatch = null;
        Status = "";
    }

    private (int Start, int Length) Range => InSelection
        ? (_selectionStart?.Offset ?? 0, Math.Max(0, (_selectionEnd?.Offset ?? 0) - (_selectionStart?.Offset ?? 0)))
        : (0, _document.TextLength);

    private StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private bool CanFind => SearchText.Length > 0;
    private bool CanReplace => CanFind && !IsReadOnly;

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _currentMatch = null;
        Closed?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanFind))]
    private void FindNext()
    {
        if (!CanFind)
            return;
        var (start, length) = Range;
        var text = _document.GetText(start, length);
        var relative = Math.Clamp(_nextOffset - start, 0, length);
        var found = text.IndexOf(SearchText, relative, Comparison);
        if (found < 0)
            found = text.IndexOf(SearchText, Comparison);
        _currentMatch = found < 0 ? null : start + found;
        Status = found < 0 ? "No matches" : "";
        if (_currentMatch is { } offset)
        {
            _nextOffset = offset + SearchText.Length;
            MatchSelected?.Invoke(offset, SearchText.Length);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private void Replace()
    {
        if (!CanReplace)
            return;
        if (_currentMatch is not { } offset)
        {
            FindNext();
            return;
        }
        _document.Replace(offset, SearchText.Length, ReplacementText);
        _nextOffset = offset + ReplacementText.Length;
        FindNext();
    }

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private void ReplaceAll()
    {
        if (!CanReplace)
            return;
        var (start, length) = Range;
        var text = _document.GetText(start, length);
        var position = 0;
        var count = 0;
        using (_document.RunUpdate())
        {
            while (position <= text.Length - SearchText.Length)
            {
                var found = text.IndexOf(SearchText, position, Comparison);
                if (found < 0)
                    break;
                _document.Replace(start + found + count * (ReplacementText.Length - SearchText.Length), SearchText.Length, ReplacementText);
                count++;
                position = found + SearchText.Length;
            }
        }
        _currentMatch = null;
        _nextOffset = start;
        Status = $"{count} replacement(s)";
    }
}
