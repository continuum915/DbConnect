using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// RAIN-P2-18 — The read-only cell value viewer: a lazy JSON tree (when the value parses as JSON)
/// and a text view, each reduced according to the value's <see cref="CellViewerTier"/>. Parsing runs
/// off the UI thread; the parsed document lives until <see cref="Dispose"/>. Copy and Save to file
/// always use <see cref="Value"/>, the exact original, never the text being shown.
/// </summary>
public sealed partial class CellValueViewerViewModel : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _columnName;
    private readonly CancellationTokenSource _cancellation = new();
    private JsonDocument? _document;
    private string? _formatted;
    private string? _openedString;
    private bool _disposed;

    public CellValueViewerViewModel(string value, string columnName)
    {
        Value = value;
        _columnName = columnName;
        Tier = CellViewerLimits.TierFor(value.Length);
        IsParsing = Tier != CellViewerTier.Preview;
        if (Tier == CellViewerTier.Preview)
        {
            SizeBanner = $"Large value: showing the first {CellViewerLimits.PreviewChars:N0} of {value.Length:N0} characters. Copy and Save to file use the full value.";
            DisplayText = PreviewOf(value);
        }
        else if (Tier == CellViewerTier.Reduced)
        {
            SizeBanner = "Large value: highlighting disabled.";
        }
    }

    /// <summary>The cell's text for the viewer, or null for values it doesn't show (NULL, and binary, which has its own viewer).</summary>
    public static string? TextOf(object? value) => value switch
    {
        null or byte[] => null,
        string text => text,
        _ => QueryResultFormattingService.FormatValue(value),
    };

    public string Value { get; }
    public CellViewerTier Tier { get; }
    public string Title => $"Value — {_columnName} ({Value.Length:N0} characters)";
    public string? SizeBanner { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowTree), nameof(CanFormat), nameof(IsHighlightingEnabled))]
    private bool _isJson;

    [ObservableProperty]
    private bool _isParsing;

    [ObservableProperty]
    private string? _parseBanner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextView))]
    private bool _isTreeView;

    [ObservableProperty]
    private bool _formatJson;

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _isWordWrapForced;

    [ObservableProperty]
    private string? _displayText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingString))]
    private string? _shownStringLabel;

    [ObservableProperty]
    private JsonTreeViewModel? _tree;

    public bool IsTextView => !IsTreeView;
    public bool CanShowTree => IsJson && Tier != CellViewerTier.Preview;
    public bool CanFormat => IsJson && Tier == CellViewerTier.Full;
    /// <summary>JSON highlighting and folding, for full-tier JSON shown as a whole.</summary>
    public bool IsHighlightingEnabled => IsJson && Tier == CellViewerTier.Full && !IsShowingString;
    public bool IsShowingString => ShownStringLabel != null;
    public bool CanChangeWordWrap => !IsWordWrapForced;

    public string SuggestedFileName => _columnName + (IsJson || (Tier == CellViewerTier.Preview && LooksLikeJson(Value)) ? ".json" : ".txt");

    /// <summary>Parses the value off the UI thread; does nothing once the viewer is closed.</summary>
    public async Task LoadAsync()
    {
        if (Tier == CellViewerTier.Preview || _disposed)
        {
            IsParsing = false;
            return;
        }

        var token = _cancellation.Token;
        var value = Value;
        var tier = Tier;
        ParseOutcome outcome;
        try
        {
            outcome = await Task.Run(() => Parse(value, tier, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed)
        {
            outcome.Document?.Dispose();
            return;
        }

        _document = outcome.Document;
        _formatted = outcome.Formatted;
        ParseBanner = outcome.Error;
        IsWordWrapForced = outcome.HasLongLine;
        WordWrap |= outcome.HasLongLine;
        IsJson = _document != null;
        if (_document != null)
        {
            Tree = new JsonTreeViewModel(_document.RootElement);
            Tree.OpenStringRequested += OnOpenStringRequested;
        }

        FormatJson = CanFormat;
        IsTreeView = CanShowTree;
        IsParsing = false;
        UpdateDisplayText();
    }

    /// <summary>Writes the exact original value as UTF-8, with no byte order mark.</summary>
    public async Task SaveToAsync(Stream stream)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(Value);
    }

    [RelayCommand]
    private void ShowTree()
    {
        if (CanShowTree)
            IsTreeView = true;
    }

    [RelayCommand]
    private void ShowText() => IsTreeView = false;

    [RelayCommand]
    private void ShowWholeValue()
    {
        _openedString = null;
        ShownStringLabel = null;
        UpdateDisplayText();
    }

    partial void OnFormatJsonChanged(bool value) => UpdateDisplayText();

    partial void OnIsWordWrapForcedChanged(bool value) => OnPropertyChanged(nameof(CanChangeWordWrap));

    partial void OnShownStringLabelChanged(string? value) => OnPropertyChanged(nameof(IsHighlightingEnabled));

    private void OnOpenStringRequested(string label, string text)
    {
        _openedString = text;
        ShownStringLabel = label;
        WordWrap |= HasLongLine(text, CellViewerTier.Reduced);
        IsTreeView = false;
        UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        if (IsParsing || Tier == CellViewerTier.Preview)
            return;

        DisplayText = _openedString
            ?? (_formatted != null && (FormatJson || Tier == CellViewerTier.Reduced) ? _formatted : Value);
    }

    private static ParseOutcome Parse(string value, CellViewerTier tier, CancellationToken token)
    {
        if (LooksLikeJson(value))
        {
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = CellViewerLimits.MaxJsonDepth });
                token.ThrowIfCancellationRequested();
                var formatted = JsonSerializer.Serialize(document.RootElement, IndentedJson);
                token.ThrowIfCancellationRequested();
                return new ParseOutcome(document, formatted, null, false);
            }
            catch (JsonException ex)
            {
                document?.Dispose();
                var line = ex.LineNumber ?? 0;
                var position = CharPositionInLine(value, line, ex.BytePositionInLine ?? 0);
                var error = $"Not valid JSON: line {line + 1}, position {position + 1}";
                return new ParseOutcome(null, null, error, HasLongLine(value, tier));
            }
            catch
            {
                document?.Dispose();
                throw;
            }
        }

        return new ParseOutcome(null, null, null, HasLongLine(value, tier));
    }

    private static bool HasLongLine(string value, CellViewerTier tier)
    {
        if (tier != CellViewerTier.Reduced)
            return false;

        var lineStart = 0;
        while (lineStart < value.Length)
        {
            var end = value.IndexOf('\n', lineStart);
            if (end < 0)
                end = value.Length;
            if (end - lineStart > CellViewerLimits.LongLineChars)
                return true;
            lineStart = end + 1;
        }

        return false;
    }

    /// <summary>The parser reports UTF-8 bytes into the line; the text view counts UTF-16 characters.</summary>
    private static long CharPositionInLine(string value, long line, long bytePosition)
    {
        var start = 0;
        for (long i = 0; i < line && start >= 0; i++)
            start = value.IndexOf('\n', start) is var newline and >= 0 ? newline + 1 : -1;
        if (start < 0)
            return bytePosition;

        var bytes = 0L;
        var index = start;
        while (index < value.Length && value[index] != '\n' && bytes < bytePosition)
        {
            var width = char.IsSurrogatePair(value, index) ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(value.AsSpan(index, width));
            index += width;
        }

        return index - start;
    }

    private static bool LooksLikeJson(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                return c is '{' or '[';
        }

        return false;
    }

    private static string PreviewOf(string value)
    {
        var length = CellViewerLimits.PreviewChars;
        if (char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
        if (Tree != null)
            Tree.OpenStringRequested -= OnOpenStringRequested;
        _document?.Dispose();
        _document = null;
    }

    private sealed record ParseOutcome(JsonDocument? Document, string? Formatted, string? Error, bool HasLongLine);
}
