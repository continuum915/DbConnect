using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>One result column in the column chooser.</summary>
public partial class ResultColumnToggle(string name, int ordinal) : ObservableObject
{
    public string Name { get; } = name;
    public int Ordinal { get; } = ordinal;

    [ObservableProperty]
    private bool _isVisible = true;
}

/// <summary>
/// RAIN-P2-17 — The query result grid's column chooser and copy options. Copy writes the visible
/// columns only, formatted by <see cref="ResultCopyFormatter"/> from the rows' raw values, or from
/// <paramref name="currentValues"/> where it returns a row's values with uncommitted edits applied.
/// </summary>
public partial class ResultGridOptionsViewModel(Func<IReadOnlyList<string>, IReadOnlyList<object?>?>? currentValues = null) : ObservableObject
{
    [ObservableProperty]
    private bool _includeHeader = true;

    [ObservableProperty]
    private CsvDelimiter _delimiter = CsvDelimiter.Tab;

    [ObservableProperty]
    private CopyQuoteCharacter _quote = CopyQuoteCharacter.None;

    [ObservableProperty]
    private bool _alwaysQuote;

    [ObservableProperty]
    private bool _sqlLiterals;

    public ObservableCollection<ResultColumnToggle> Columns { get; } = [];

    public IReadOnlyList<CsvDelimiter> DelimiterOptions { get; } = Enum.GetValues<CsvDelimiter>();
    public IReadOnlyList<CopyQuoteCharacter> QuoteOptions { get; } = Enum.GetValues<CopyQuoteCharacter>();

    /// <summary>Raised when a column is shown or hidden, so the view can update the grid's columns.</summary>
    public event Action? ColumnVisibilityChanged;

    public IReadOnlyList<int> VisibleOrdinals => Columns.Where(column => column.IsVisible).Select(column => column.Ordinal).ToList();

    public ResultCopyOptions CopyOptions => new()
    {
        IncludeHeader = IncludeHeader,
        Delimiter = Delimiter,
        Quote = Quote,
        AlwaysQuote = AlwaysQuote,
        SqlLiterals = SqlLiterals,
        Dialect = _dialect
    };

    private DatabaseProviderCapabilities? _dialect;

    /// <summary>
    /// Loads a new result's columns, and the connection whose literal style SQL-literal copy follows.
    /// Re-running a query that returns the same columns keeps the columns the user hid; any other
    /// column list shows every column again.
    /// </summary>
    public void Load(IReadOnlyList<string> names, DatabaseProviderCapabilities? capabilities)
    {
        _dialect = capabilities;
        if (Columns.Select(column => column.Name).SequenceEqual(names))
            return;

        foreach (var column in Columns)
            column.PropertyChanged -= OnColumnPropertyChanged;
        Columns.Clear();

        for (var i = 0; i < names.Count; i++)
        {
            var column = new ResultColumnToggle(names[i], i);
            column.PropertyChanged += OnColumnPropertyChanged;
            Columns.Add(column);
        }

        ColumnVisibilityChanged?.Invoke();
    }

    [RelayCommand]
    private void ShowAllColumns()
    {
        foreach (var column in Columns)
            column.IsVisible = true;
    }

    public string? BuildCellText(IReadOnlyList<string>? row, int ordinal) =>
        row == null || ordinal < 0 || ordinal >= row.Count ? null : ResultCopyFormatter.FormatCell(ValueAt(row, ordinal), CopyOptions);

    /// <summary>A grid row's raw value at <paramref name="ordinal"/>, with any uncommitted edit applied.</summary>
    public object? ValueAt(IReadOnlyList<string> row, int ordinal)
    {
        var raw = ValuesOf(row);
        return ordinal >= 0 && ordinal < raw.Count ? raw[ordinal] : null;
    }

    /// <summary>The given grid rows, visible columns only; null when no column is visible.</summary>
    public string? BuildRowsText(IEnumerable<IReadOnlyList<string>> rows)
    {
        var ordinals = VisibleOrdinals;
        if (ordinals.Count == 0)
            return null;

        var headers = ordinals.Select(ordinal => Columns[ordinal].Name).ToList();
        var values = rows.Select(row =>
        {
            var raw = ValuesOf(row);
            return (IReadOnlyList<object?>)ordinals.Select(ordinal => ordinal < raw.Count ? raw[ordinal] : null).ToList();
        });
        return ResultCopyFormatter.FormatRows(headers, values, CopyOptions);
    }

    private IReadOnlyList<object?> ValuesOf(IReadOnlyList<string> row) =>
        currentValues?.Invoke(row) ?? QueryResultDisplayRow.RawValuesOf(row) ?? row.ToList<object?>();

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultColumnToggle.IsVisible))
            ColumnVisibilityChanged?.Invoke();
    }
}
