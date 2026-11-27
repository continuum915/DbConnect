using System;
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
/// The table designer's check constraints: the loaded ones with their edits and the added ones.
/// <see cref="Changed"/> fires on every edit so the plan can follow.
/// </summary>
public sealed partial class TableChecksEditor : ObservableObject
{
    public TableChecksEditor(TableCheckRules rules)
    {
        Rules = rules;
        Items.CollectionChanged += OnItemsChanged;
    }

    public TableCheckRules Rules { get; }

    /// <summary>The engine's capabilities, or why this table's constraints cannot change at all.</summary>
    public string Summary => Unsupported is { } reason ? $"Checks of this table cannot be changed after creation: {reason}." : Rules.Summary;

    /// <summary>The loaded table's reason constraints cannot change after creation; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _unsupported;

    public ObservableCollection<EditableCheck> Items { get; } = new();

    [ObservableProperty] private EditableCheck? _selected;

    public event Action? Changed;

    public void Load(TableDesign design)
    {
        Clear();
        Unsupported = design.UnsupportedReason(TableDesignFeature.ConstraintChanges);
        foreach (var check in design.Checks)
            Items.Add(new EditableCheck { Loaded = check, Name = check.Name, Expression = check.Expression });
    }

    /// <summary>Removes every check one by one, so each one's handler is detached.</summary>
    public void Clear()
    {
        while (Items.Count > 0) Items.RemoveAt(Items.Count - 1);
        Selected = null;
    }

    [RelayCommand]
    private void AddCheck()
    {
        var check = new EditableCheck();
        Items.Add(check);
        Selected = check;
    }

    /// <summary>Removes an added check; marks a loaded one dropped, or restores it.</summary>
    [RelayCommand]
    private void DropCheck(EditableCheck check)
    {
        if (check.IsNew) Items.Remove(check);
        else check.IsDropped = !check.IsDropped;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var check in e.OldItems?.OfType<EditableCheck>() ?? []) check.PropertyChanged -= OnCheckChanged;
        foreach (var check in e.NewItems?.OfType<EditableCheck>() ?? []) check.PropertyChanged += OnCheckChanged;
        if (Selected != null && !Items.Contains(Selected)) Selected = null;
        Changed?.Invoke();
    }

    private void OnCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditableCheck.Status)) Changed?.Invoke();
    }
}
