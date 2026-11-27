using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>The rows and cells a record (form) view shows and edits, supplied by each result grid.</summary>
public interface IRecordSource
{
    int Count { get; }
    IReadOnlyList<string> Columns { get; }
    object? Value(int row, int column);
    bool CanEdit(int row, int column);
    /// <summary>Parses and stores the text; returns why it was refused, or null.</summary>
    string? SetText(int row, int column, string? text);
    void SetNull(int row, int column);
    bool IsChanged(int row, int column);
}

public sealed partial class RecordField(RecordViewModel owner, int column, string name) : ObservableObject
{
    public string Name { get; } = name;
    public int Column { get; } = column;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isNull;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private bool _isChanged;
    [ObservableProperty] private string? _error;
    private bool _loading;

    internal void Load(object? value, bool editable, bool changed)
    {
        _loading = true;
        Text = value == null ? string.Empty : QueryResultFormattingService.FormatValue(value);
        IsNull = value == null;
        IsEditable = editable;
        IsChanged = changed;
        Error = null;
        _loading = false;
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading) owner.Edit(this, value);
    }

    [RelayCommand]
    private void SetNull() => owner.EditNull(this);
}

/// <summary>
/// One row at a time as name/value pairs with first/previous/next/last, for wide rows. Edits go through the
/// grid's own setters, so they are the same pending edits the grid shows and commits.
/// </summary>
public sealed partial class RecordViewModel(IRecordSource source) : ObservableObject
{
    public ObservableCollection<RecordField> Fields { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(FirstCommand), nameof(PreviousCommand), nameof(NextCommand), nameof(LastCommand))]
    private int _position;

    /// <summary>True while the tab shows the record view instead of the grid.</summary>
    [ObservableProperty] private bool _isActive;

    public string PositionText => source.Count == 0 ? "No rows" : $"Row {Position + 1:N0} of {source.Count:N0}";

    private int _gridRow;

    /// <summary>The grid's current row, where the record view opens.</summary>
    public void SetGridRow(int row) => _gridRow = row;

    /// <summary>Switches between the grid and the record view, opening at the grid's current row.</summary>
    [RelayCommand]
    private void Toggle()
    {
        IsActive = !IsActive;
        if (IsActive) Show(Math.Max(0, _gridRow));
    }

    public void Show(int row)
    {
        Position = Math.Clamp(row, 0, Math.Max(0, source.Count - 1));
        Reload();
    }

    public void Reload()
    {
        var columns = source.Columns;
        if (Fields.Count != columns.Count || Fields.Select(f => f.Name).Where((n, i) => n != columns[i]).Any())
        {
            Fields.Clear();
            for (var i = 0; i < columns.Count; i++) Fields.Add(new RecordField(this, i, columns[i]));
        }
        OnPropertyChanged(nameof(PositionText));
        if (source.Count == 0) return;
        foreach (var field in Fields)
            field.Load(source.Value(Position, field.Column), source.CanEdit(Position, field.Column), source.IsChanged(Position, field.Column));
    }

    internal void Edit(RecordField field, string text)
    {
        if (source.Count == 0) return;
        field.Error = source.SetText(Position, field.Column, text);
        if (field.Error == null) Refresh(field);
    }

    internal void EditNull(RecordField field)
    {
        if (source.Count == 0 || !field.IsEditable) return;
        source.SetNull(Position, field.Column);
        Refresh(field);
    }

    private void Refresh(RecordField field) =>
        field.Load(source.Value(Position, field.Column), source.CanEdit(Position, field.Column), source.IsChanged(Position, field.Column));

    private bool CanBack() => Position > 0;
    private bool CanForward() => Position < source.Count - 1;

    [RelayCommand(CanExecute = nameof(CanBack))] private void First() => Show(0);
    [RelayCommand(CanExecute = nameof(CanBack))] private void Previous() => Show(Position - 1);
    [RelayCommand(CanExecute = nameof(CanForward))] private void Next() => Show(Position + 1);
    [RelayCommand(CanExecute = nameof(CanForward))] private void Last() => Show(source.Count - 1);
}

/// <summary>Bold text for record fields with a pending edit.</summary>
public static class RecordFieldStyles
{
    public static Avalonia.Data.Converters.IValueConverter ChangedWeight { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(changed =>
            changed ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal);
}
