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
/// The table designer's primary and unique keys: the loaded keys with their edits and the added
/// ones, the selected key's ordered members, and the columns' PK checkboxes kept in step with the
/// primary key's members. <see cref="Changed"/> fires on every edit so the plan can follow.
/// </summary>
public sealed partial class TableKeysEditor : ObservableObject
{
    private readonly Func<IEnumerable<ColumnDefinition>> _columns;
    private bool _syncing;

    public TableKeysEditor(Func<IEnumerable<ColumnDefinition>> columns, TableKeyRules rules)
    {
        _columns = columns;
        Rules = rules;
        Items.CollectionChanged += OnItemsChanged;
    }

    public TableKeyRules Rules { get; }

    /// <summary>The engine's capabilities, or why this table's constraints cannot change at all.</summary>
    public string Summary => Unsupported is { } reason ? $"Keys of this table cannot be changed after creation: {reason}." : Rules.Summary;

    /// <summary>The loaded table's reason constraints cannot change after creation; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _unsupported;

    public ObservableCollection<KeyDefinition> Items { get; } = new();

    /// <summary>Names of the table's columns that are not being dropped, offered as key members.</summary>
    public ObservableCollection<string> ColumnNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private KeyDefinition? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private string? _newMember;

    [ObservableProperty] private string? _selectedMember;

    public bool HasSelection => Selected != null;

    public event Action? Changed;

    /// <summary>Replaces the keys with the loaded design's, each with the index that backs it.</summary>
    public void Load(TableDesign design)
    {
        Clear();
        Unsupported = design.UnsupportedReason(TableDesignFeature.ConstraintChanges);
        if (design.PrimaryKey is { } pk)
            Items.Add(Create(KeyKind.Primary, pk, design.Indexes.FirstOrDefault(i => i.Origin == TableDesignIndexOrigin.PrimaryKey)));
        foreach (var key in design.UniqueKeys)
            Items.Add(Create(KeyKind.Unique, key, design.Indexes.FirstOrDefault(i => i.Origin == TableDesignIndexOrigin.UniqueKey
                && (key.Name != null ? i.Name == key.Name : i.Parts.Select(p => p.Column).SequenceEqual(key.Columns)))));
        Selected = null;
        RefreshColumnNames();
        // The loaded key is the baseline: the columns' originals agree with it, so loading plans nothing.
        SyncPrimaryKeyFlags(originals: true);
    }

    /// <summary>Removes every key one by one, so each one's handlers are detached.</summary>
    public void Clear()
    {
        while (Items.Count > 0) Items.RemoveAt(Items.Count - 1);
        Selected = null;
    }

    private KeyDefinition Create(KeyKind kind, TableDesignKey? loaded = null, TableDesignIndex? backing = null) =>
        new(kind, loaded?.Columns)
        {
            Name = loaded?.Name,
            Loaded = loaded,
            BackingIndex = backing,
            NameLocked = kind == KeyKind.Primary && !Rules.PrimaryKeyNamed
        };

    private KeyDefinition? PrimaryKey => Items.FirstOrDefault(k => k.Kind == KeyKind.Primary && !k.IsDropped);

    [RelayCommand]
    private void AddPrimaryKey()
    {
        var key = PrimaryKey ?? Create(KeyKind.Primary);
        if (!Items.Contains(key)) Items.Add(key);
        Selected = key;
    }

    [RelayCommand]
    private void AddUniqueKey()
    {
        var key = Create(KeyKind.Unique);
        Items.Add(key);
        Selected = key;
    }

    /// <summary>Removes an added key; marks a loaded one dropped, or restores it.</summary>
    [RelayCommand]
    private void DropKey(KeyDefinition key)
    {
        if (key.IsNew) Items.Remove(key);
        else key.IsDropped = !key.IsDropped;
        SyncPrimaryKeyFlags();
    }

    private bool CanAddMember() => Selected != null && !string.IsNullOrWhiteSpace(NewMember);

    [RelayCommand(CanExecute = nameof(CanAddMember))]
    private void AddMember()
    {
        if (Selected is not { } key || string.IsNullOrWhiteSpace(NewMember)) return;
        if (!key.Columns.Contains(NewMember, StringComparer.OrdinalIgnoreCase)) key.Columns.Add(NewMember);
        NewMember = null;
    }

    [RelayCommand]
    private void RemoveMember(string member) => Selected?.Columns.Remove(member);

    [RelayCommand]
    private void MoveMemberUp(string member) => Move(member, -1);

    [RelayCommand]
    private void MoveMemberDown(string member) => Move(member, 1);

    private void Move(string member, int by)
    {
        if (Selected is not { } key) return;
        var from = key.Columns.IndexOf(member);
        var to = from + by;
        if (from < 0 || to < 0 || to >= key.Columns.Count) return;
        key.Columns.Move(from, to);
        SelectedMember = member;
    }

    /// <summary>A column's PK checkbox changed: it joins the primary key at the end, or leaves it.</summary>
    public void OnPrimaryKeyFlag(ColumnDefinition column)
    {
        if (_syncing) return;
        var key = PrimaryKey;
        if (column.IsPrimaryKey)
        {
            if (key == null) Items.Add(key = Create(KeyKind.Primary));
            if (!key.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)) key.Columns.Add(column.Name);
        }
        else if (key != null)
        {
            var member = key.Columns.FirstOrDefault(c => c.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            if (member != null) key.Columns.Remove(member);
            // Unchecking the last column removes the primary key.
            if (key.Columns.Count == 0)
            {
                if (key.IsNew) Items.Remove(key);
                else key.IsDropped = true;
            }
        }
    }

    private string? _renaming;

    /// <summary>A column is about to be renamed: remember the name its keys list.</summary>
    public void OnColumnRenaming(ColumnDefinition column) => _renaming = column.Name;

    /// <summary>A column was renamed: its keys list it by the new name, as the engine's would.</summary>
    public void OnColumnRenamed(ColumnDefinition column)
    {
        if (_renaming is not { } old || string.IsNullOrWhiteSpace(old)) return;
        _renaming = null;
        foreach (var key in Items)
            for (var i = 0; i < key.Columns.Count; i++)
                if (key.Columns[i].Equals(old, StringComparison.OrdinalIgnoreCase)) key.Columns[i] = column.Name;
    }

    /// <summary>An added column was removed again: it leaves every key.</summary>
    public void OnColumnRemoved(ColumnDefinition column)
    {
        foreach (var key in Items)
            if (key.Columns.FirstOrDefault(c => c.Equals(column.Name, StringComparison.OrdinalIgnoreCase)) is { } member)
                key.Columns.Remove(member);
    }

    public void RefreshColumnNames()
    {
        var names = _columns().Where(c => !c.IsDropped && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name).ToList();
        if (names.SequenceEqual(ColumnNames)) return;
        ColumnNames.Clear();
        foreach (var name in names) ColumnNames.Add(name);
    }

    /// <summary>Sets each column's PK checkbox from the primary key's members.</summary>
    private void SyncPrimaryKeyFlags(bool originals = false)
    {
        var members = PrimaryKey?.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        _syncing = true;
        try
        {
            foreach (var column in _columns())
            {
                column.IsPrimaryKey = members.Contains(column.Name);
                if (originals && column.Original != null) column.Original.IsPrimaryKey = column.IsPrimaryKey;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var key in e.OldItems?.OfType<KeyDefinition>() ?? [])
        {
            key.PropertyChanged -= OnKeyChanged;
            key.Columns.CollectionChanged -= OnMembersChanged;
        }
        foreach (var key in e.NewItems?.OfType<KeyDefinition>() ?? [])
        {
            key.PropertyChanged += OnKeyChanged;
            key.Columns.CollectionChanged += OnMembersChanged;
        }
        if (Selected != null && !Items.Contains(Selected)) Selected = null;
        Changed?.Invoke();
    }

    private void OnKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyDefinition.Name) or nameof(KeyDefinition.IsDropped)) Changed?.Invoke();
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncPrimaryKeyFlags();
        Changed?.Invoke();
    }
}
