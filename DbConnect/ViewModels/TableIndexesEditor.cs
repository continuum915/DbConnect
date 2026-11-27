using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

/// <summary>
/// The table designer's indexes: the loaded ones with their edits (indexes backing a key read-only), the
/// added ones, and the selected index's ordered key parts (columns or expressions) and their directions, its
/// included columns, filter and method, as far as the engine's rules offer them. <see cref="Changed"/> fires
/// on every edit so the plan can follow.
/// </summary>
public sealed partial class TableIndexesEditor : ObservableObject
{
    private readonly Func<IEnumerable<ColumnDefinition>> _columns;
    private string? _renaming;

    public TableIndexesEditor(Func<IEnumerable<ColumnDefinition>> columns, TableIndexRules rules)
    {
        _columns = columns;
        Rules = rules;
        Items.CollectionChanged += OnItemsChanged;
    }

    public TableIndexRules Rules { get; }

    /// <summary>The engine's index capabilities, shown on the Indexes tab.</summary>
    public string Summary => Rules.Summary;

    public ObservableCollection<EditableIndex> Items { get; } = new();

    /// <summary>The methods the engine offers; empty when it has none to choose.</summary>
    public IReadOnlyList<string> Methods => Rules.Methods.Select(m => m.Name).ToList();

    public bool HasExpressions => Rules.Expressions;
    public bool HasIncludedColumns => Rules.IncludedColumns;
    public bool HasFilters => Rules.Filters;
    public bool HasMethods => Rules.Methods.Count > 0;

    /// <summary>Names of the table's columns that are not being dropped, offered as key and included columns.</summary>
    public ObservableCollection<string> ColumnNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanEditSelection), nameof(SelectedMethod), nameof(CanEditMethod))]
    [NotifyCanExecuteChangedFor(nameof(AddPartCommand), nameof(AddExpressionCommand), nameof(AddIncludedCommand))]
    private EditableIndex? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPartCommand))]
    private string? _newPart;

    [ObservableProperty] private EditableIndexPart? _selectedPart;

    /// <summary>An expression typed to add as the selected index's last key part.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddExpressionCommand))]
    private string? _newExpression;

    /// <summary>A column picked to add to the selected index's included columns.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddIncludedCommand))]
    private string? _newIncluded;

    [ObservableProperty] private string? _selectedIncluded;

    public bool HasSelection => Selected != null;

    /// <summary>The selected index can be edited: it does not back a key.</summary>
    public bool CanEditSelection => Selected is { IsEditable: true };

    /// <summary>The selected index's method can be chosen: it is editable and has no method, or one the engine offers.</summary>
    public bool CanEditMethod => CanEditSelection && (Selected!.Method == null || Rules.Method(Selected.Method) != null);

    /// <summary>
    /// The selected index's method. Choosing none (as a list does when the index has a method it does not offer)
    /// changes nothing, so a method the designer does not offer is kept.
    /// </summary>
    public string? SelectedMethod
    {
        get => Selected?.Method;
        set
        {
            if (value != null && CanEditMethod && Rules.Method(value) is { } method) Selected!.Method = method.Name;
            OnPropertyChanged();
        }
    }

    public event Action? Changed;

    /// <summary>
    /// Replaces the indexes with the loaded design's; <paramref name="storedSql"/> gives the engine's own
    /// CREATE INDEX text by index name, where the engine keeps one (SQLite rebuilds replay it).
    /// </summary>
    public void Load(TableDesign design, IReadOnlyDictionary<string, string?> storedSql)
    {
        Clear();
        foreach (var index in design.Indexes)
            Items.Add(new EditableIndex(index, storedSql.TryGetValue(index.Name, out var sql) ? sql : null));
        RefreshColumnNames();
    }

    /// <summary>Removes every index one by one, so each one's handlers are detached.</summary>
    public void Clear()
    {
        while (Items.Count > 0) Items.RemoveAt(Items.Count - 1);
        Selected = null;
    }

    [RelayCommand]
    private void AddIndex()
    {
        var index = new EditableIndex(method: Rules.Methods.FirstOrDefault()?.Name);
        Items.Add(index);
        Selected = index;
    }

    /// <summary>Removes an added index; marks a loaded one dropped, or restores it. Indexes backing a key stay.</summary>
    [RelayCommand]
    private void DropIndex(EditableIndex index)
    {
        if (index.IsReadOnly) return;
        if (index.IsNew) Items.Remove(index);
        else index.IsDropped = !index.IsDropped;
    }

    private bool CanAddPart() => CanEditSelection && !string.IsNullOrWhiteSpace(NewPart);

    [RelayCommand(CanExecute = nameof(CanAddPart))]
    private void AddPart()
    {
        if (Selected is not { IsEditable: true } index || string.IsNullOrWhiteSpace(NewPart)) return;
        if (!index.Parts.Any(p => string.Equals(p.Column, NewPart, StringComparison.OrdinalIgnoreCase)))
            index.Parts.Add(new EditableIndexPart(NewPart));
        NewPart = null;
    }

    private bool CanAddExpression() => CanEditSelection && Rules.Expressions && !string.IsNullOrWhiteSpace(NewExpression);

    [RelayCommand(CanExecute = nameof(CanAddExpression))]
    private void AddExpression()
    {
        if (!CanAddExpression()) return;
        var part = new EditableIndexPart(null, NewExpression!.Trim());
        Selected!.Parts.Add(part);
        SelectedPart = part;
        NewExpression = null;
    }

    private bool CanAddIncluded() => CanEditSelection && Rules.IncludedColumns && !string.IsNullOrWhiteSpace(NewIncluded);

    [RelayCommand(CanExecute = nameof(CanAddIncluded))]
    private void AddIncluded()
    {
        if (!CanAddIncluded()) return;
        if (!Selected!.IncludedColumns.Contains(NewIncluded!, StringComparer.OrdinalIgnoreCase)) Selected.IncludedColumns.Add(NewIncluded!);
        NewIncluded = null;
    }

    [RelayCommand]
    private void RemoveIncluded(string? column)
    {
        if (CanEditSelection && column != null) Selected!.IncludedColumns.Remove(column);
    }

    [RelayCommand]
    private void RemovePart(EditableIndexPart? part)
    {
        if (CanEditSelection && part != null) Selected!.Parts.Remove(part);
    }

    [RelayCommand]
    private void MovePartUp(EditableIndexPart? part) => Move(part, -1);

    [RelayCommand]
    private void MovePartDown(EditableIndexPart? part) => Move(part, 1);

    private void Move(EditableIndexPart? part, int by)
    {
        if (!CanEditSelection || part == null) return;
        var parts = Selected!.Parts;
        var from = parts.IndexOf(part);
        var to = from + by;
        if (from < 0 || to < 0 || to >= parts.Count) return;
        parts.Move(from, to);
        SelectedPart = part;
    }

    /// <summary>A column is about to be renamed: remember the name its indexes list.</summary>
    public void OnColumnRenaming(ColumnDefinition column) => _renaming = column.Name;

    /// <summary>A column was renamed: its indexes list it by the new name, as the engine's would.</summary>
    public void OnColumnRenamed(ColumnDefinition column)
    {
        if (_renaming is not { } old || string.IsNullOrWhiteSpace(old)) return;
        _renaming = null;
        foreach (var part in Items.SelectMany(i => i.Parts).Where(p => old.Equals(p.Column, StringComparison.OrdinalIgnoreCase)))
            part.Column = column.Name;
        foreach (var index in Items) index.RenameIncluded(old, column.Name);
    }

    /// <summary>An added column was removed again: it leaves every index.</summary>
    public void OnColumnRemoved(ColumnDefinition column)
    {
        foreach (var index in Items)
        {
            foreach (var part in index.Parts.Where(p => column.Name.Equals(p.Column, StringComparison.OrdinalIgnoreCase)).ToList())
                index.Parts.Remove(part);
            foreach (var included in index.IncludedColumns.Where(c => column.Name.Equals(c, StringComparison.OrdinalIgnoreCase)).ToList())
                index.IncludedColumns.Remove(included);
        }
    }

    public void RefreshColumnNames()
    {
        var names = _columns().Where(c => !c.IsDropped && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name).ToList();
        if (names.SequenceEqual(ColumnNames)) return;
        ColumnNames.Clear();
        foreach (var name in names) ColumnNames.Add(name);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var index in e.OldItems?.OfType<EditableIndex>() ?? []) index.PropertyChanged -= OnIndexChanged;
        foreach (var index in e.NewItems?.OfType<EditableIndex>() ?? []) index.PropertyChanged += OnIndexChanged;
        if (Selected != null && !Items.Contains(Selected)) Selected = null;
        Changed?.Invoke();
    }

    private void OnIndexChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableIndex.Name) or nameof(EditableIndex.IsUnique) or nameof(EditableIndex.IsDropped)
            or nameof(EditableIndex.PartsDisplay) or nameof(EditableIndex.Filter) or nameof(EditableIndex.Method)
            or nameof(EditableIndex.IncludedDisplay))
            Changed?.Invoke();
        if (e.PropertyName == nameof(EditableIndex.Method) && sender == Selected)
        {
            OnPropertyChanged(nameof(SelectedMethod));
            OnPropertyChanged(nameof(CanEditMethod));
        }
    }
}
