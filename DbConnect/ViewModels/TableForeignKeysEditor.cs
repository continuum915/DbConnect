using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

/// <summary>
/// The table designer's foreign keys: the loaded ones with their edits and the added ones, the
/// selected key's ordered column pairs, and the tables it can reference. The referenced tables come
/// from the metadata catalog and each chosen table's columns and keys from its design, both loaded in
/// the background; a failed load is shown with a retry, never as an empty list.
/// </summary>
public sealed partial class TableForeignKeysEditor : ObservableObject
{
    private readonly Func<IEnumerable<ColumnDefinition>> _columns;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DatabaseObjectIdentity>>>? _listTables;
    private readonly Func<DatabaseObjectIdentity, CancellationToken, Task<TableDesign>>? _readDesign;
    private readonly Func<DatabaseObjectIdentity?> _self;
    private readonly List<(DatabaseObjectIdentity Table, ForeignKeyTarget Target)> _targets = [];
    private CancellationTokenSource? _listing;
    private string? _renaming;

    public TableForeignKeysEditor(Func<IEnumerable<ColumnDefinition>> columns, TableForeignKeyRules rules,
        Func<CancellationToken, Task<IReadOnlyList<DatabaseObjectIdentity>>>? listTables,
        Func<DatabaseObjectIdentity, CancellationToken, Task<TableDesign>>? readDesign,
        Func<DatabaseObjectIdentity?> self)
    {
        _columns = columns;
        _listTables = listTables;
        _readDesign = readDesign;
        _self = self;
        Rules = rules;
        Items.CollectionChanged += OnItemsChanged;
    }

    public TableForeignKeyRules Rules { get; }

    /// <summary>The engine's capabilities, or why this table's constraints cannot change at all.</summary>
    public string Summary => Unsupported is { } reason ? $"Foreign keys of this table cannot be changed after creation: {reason}." : Rules.Summary;

    /// <summary>The loaded table's reason constraints cannot change after creation; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _unsupported;
    public IReadOnlyList<string> Actions => Rules.Actions;

    public ObservableCollection<EditableForeignKey> Items { get; } = new();

    /// <summary>Columns of this table that are not being dropped.</summary>
    public ObservableCollection<string> LocalColumns { get; } = new();

    /// <summary>Columns of the selected key's referenced table.</summary>
    public ObservableCollection<string> ReferencedColumns { get; } = new();

    /// <summary>Tables a foreign key can reference.</summary>
    public ObservableCollection<ReferenceTarget> Targets { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingTargets), nameof(TargetsFailed))]
    private MetadataLoadState _targetsState = MetadataLoadState.NotLoaded;

    [ObservableProperty] private string? _targetsError;

    public bool IsLoadingTargets => TargetsState == MetadataLoadState.Loading;
    public bool TargetsFailed => TargetsState == MetadataLoadState.Failed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AddPairCommand))]
    private EditableForeignKey? _selected;

    [ObservableProperty] private ReferenceTarget? _selectedTarget;
    [ObservableProperty] private ForeignKeyPair? _selectedPair;

    /// <summary>Why the selected key's referenced columns are not shown, with a retry; null when they are.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetColumnsFailed))]
    private string? _targetColumnsError;

    public bool TargetColumnsFailed => TargetColumnsError != null;
    public bool HasSelection => Selected != null;

    public event Action? Changed;

    /// <summary>What is known about a referenced table; null while it has not been asked for.</summary>
    public ForeignKeyTarget? TargetFor(DatabaseObjectIdentity table) =>
        _targets.FirstOrDefault(t => TableIdentity.Same(t.Table, table)).Target;

    public void Load(TableDesign design)
    {
        Clear();
        Unsupported = design.UnsupportedReason(TableDesignFeature.ConstraintChanges);
        foreach (var key in design.ForeignKeys)
            Items.Add(new EditableForeignKey(key.Columns)
            {
                Loaded = key,
                Name = key.Name,
                ReferencedTable = key.ReferencedTable,
                OnDelete = key.OnDelete,
                OnUpdate = key.OnUpdate
            });
        RefreshColumnNames();
    }

    /// <summary>Removes every key one by one, detaching its handlers, and forgets the referenced tables read so far.</summary>
    public void Clear()
    {
        while (Items.Count > 0) Items.RemoveAt(Items.Count - 1);
        Selected = null;
        _targets.Clear();
    }

    private bool IsSelf(DatabaseObjectIdentity? table) => _self() is { } self && TableIdentity.Same(table, self);

    /// <summary>Lists the tables a foreign key can reference; a failure is kept with its reason for a retry.</summary>
    [RelayCommand]
    public async Task LoadTargetsAsync()
    {
        if (_listTables == null) return;
        _listing?.Cancel();
        var listing = _listing = new CancellationTokenSource();
        TargetsState = MetadataLoadState.Loading;
        TargetsError = null;
        try
        {
            var tables = await _listTables(listing.Token);
            if (listing.IsCancellationRequested) return;
            Targets.Clear();
            foreach (var table in tables) Targets.Add(new ReferenceTarget(table));
            if (_self() is { } self && !Targets.Any(t => TableIdentity.Same(t.Identity, self))) Targets.Add(new ReferenceTarget(self));
            TargetsState = MetadataLoadState.Loaded;
            SyncSelectedTarget();
        }
        catch (OperationCanceledException) when (listing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (listing.IsCancellationRequested) return;
            TargetsError = DiagnosticRedactor.Redact(ex.Message);
            TargetsState = MetadataLoadState.Failed;
        }
    }

    [RelayCommand]
    private void AddForeignKey()
    {
        var key = new EditableForeignKey { Pairs = { new ForeignKeyPair() } };
        Items.Add(key);
        Selected = key;
    }

    /// <summary>Removes an added key; marks a loaded one dropped, or restores it.</summary>
    [RelayCommand]
    private void DropForeignKey(EditableForeignKey key)
    {
        if (key.IsNew) Items.Remove(key);
        else key.IsDropped = !key.IsDropped;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddPair()
    {
        if (Selected is not { } key) return;
        var pair = new ForeignKeyPair();
        key.Pairs.Add(pair);
        SelectedPair = pair;
    }

    [RelayCommand]
    private void RemovePair(ForeignKeyPair? pair)
    {
        if (pair != null) Selected?.Pairs.Remove(pair);
    }

    [RelayCommand]
    private void MovePairUp(ForeignKeyPair? pair) => Move(pair, -1);

    [RelayCommand]
    private void MovePairDown(ForeignKeyPair? pair) => Move(pair, 1);

    private void Move(ForeignKeyPair? pair, int by)
    {
        if (Selected is not { } key || pair == null) return;
        var from = key.Pairs.IndexOf(pair);
        var to = from + by;
        if (from < 0 || to < 0 || to >= key.Pairs.Count) return;
        key.Pairs.Move(from, to);
        SelectedPair = pair;
    }

    [RelayCommand]
    private Task RetryTargetColumnsAsync()
    {
        if (Selected?.ReferencedTable is not { } table) return Task.CompletedTask;
        _targets.RemoveAll(t => TableIdentity.Same(t.Table, table));
        return LoadTargetAsync(table);
    }

    partial void OnSelectedChanged(EditableForeignKey? value)
    {
        SyncSelectedTarget();
        RefreshReferencedColumns();
    }

    partial void OnSelectedTargetChanged(ReferenceTarget? value)
    {
        if (Selected is not { } key || value == null || TableIdentity.Same(key.ReferencedTable, value.Identity)) return;
        key.ReferencedTable = value.Identity;
    }

    /// <summary>Shows the selected key's referenced table in the picker, adding a loaded target the catalog did not list.</summary>
    private void SyncSelectedTarget()
    {
        if (Selected?.ReferencedTable is not { } table)
        {
            SelectedTarget = null;
            return;
        }
        var target = Targets.FirstOrDefault(t => TableIdentity.Same(t.Identity, table));
        if (target == null) Targets.Add(target = new ReferenceTarget(table));
        SelectedTarget = target;
    }

    private void RefreshReferencedColumns()
    {
        ReferencedColumns.Clear();
        TargetColumnsError = null;
        if (Selected?.ReferencedTable is not { } table) return;
        var columns = IsSelf(table)
            ? _columns().Where(c => !c.IsDropped).Select(c => c.Name)
            : TargetFor(table) switch
            {
                { State: MetadataLoadState.Loaded } loaded => loaded.Columns,
                { State: MetadataLoadState.Failed } failed => Fail(failed.Error),
                _ => []
            };
        foreach (var column in columns) ReferencedColumns.Add(column);

        IEnumerable<string> Fail(string? error)
        {
            TargetColumnsError = $"The columns of {TableDesignText.Qualified(table)} could not be read: {error}";
            return [];
        }
    }

    /// <summary>
    /// Takes what is known about a referenced table that does not exist yet, such as one a schema synchronization
    /// creates in the same run, so a key to it can be checked and planned.
    /// </summary>
    public void Know(DatabaseObjectIdentity table, ForeignKeyTarget target)
    {
        _targets.RemoveAll(t => TableIdentity.Same(t.Table, table));
        _targets.Add((table, target));
    }

    /// <summary>Loads every referenced table of new and changed keys that is not known yet, and waits for them.</summary>
    public async Task EnsureTargetsAsync()
    {
        foreach (var key in Items.Where(k => !k.IsDropped && (k.IsNew || k.DefinitionChanged)).ToList())
            if (key.ReferencedTable is { } table && !IsSelf(table) && TargetFor(table) is null or { State: MetadataLoadState.Loading })
            {
                _targets.RemoveAll(t => TableIdentity.Same(t.Table, table));
                await LoadTargetAsync(table);
            }
    }

    /// <summary>Starts loading the referenced tables of new and changed keys that are not known yet.</summary>
    private void EnsureTargets()
    {
        foreach (var key in Items.Where(k => !k.IsDropped && (k.IsNew || k.DefinitionChanged)))
            if (key.ReferencedTable is { } table && !IsSelf(table) && TargetFor(table) == null)
                _ = LoadTargetAsync(table);
    }

    private async Task LoadTargetAsync(DatabaseObjectIdentity table)
    {
        if (_readDesign == null) return;
        _targets.Add((table, ForeignKeyTarget.Loading));
        ForeignKeyTarget result;
        try
        {
            result = ForeignKeyTarget.From(await _readDesign(table, CancellationToken.None));
        }
        catch (Exception ex)
        {
            result = ForeignKeyTarget.Failed(ex is TableDefinitionUnavailableException ? ex.Message : DiagnosticRedactor.Redact(ex.Message));
        }
        var index = _targets.FindIndex(t => TableIdentity.Same(t.Table, table));
        if (index >= 0) _targets[index] = (table, result);
        if (Selected?.ReferencedTable is { } selected && TableIdentity.Same(selected, table)) RefreshReferencedColumns();
        Changed?.Invoke();
    }

    public void RefreshColumnNames()
    {
        var names = _columns().Where(c => !c.IsDropped && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name).ToList();
        if (!names.SequenceEqual(LocalColumns))
        {
            LocalColumns.Clear();
            foreach (var name in names) LocalColumns.Add(name);
        }
        if (IsSelf(Selected?.ReferencedTable)) RefreshReferencedColumns();
    }

    public void OnColumnRenaming(ColumnDefinition column) => _renaming = column.Name;

    /// <summary>A column was renamed: pairs name it by its new name, as the engine's would (also as a referenced column of this table).</summary>
    public void OnColumnRenamed(ColumnDefinition column)
    {
        if (_renaming is not { } old || string.IsNullOrWhiteSpace(old)) return;
        _renaming = null;
        foreach (var key in Items)
            foreach (var pair in key.Pairs)
            {
                if (pair.Column.Equals(old, StringComparison.OrdinalIgnoreCase)) pair.Column = column.Name;
                if (IsSelf(key.ReferencedTable) && pair.ReferencedColumn.Equals(old, StringComparison.OrdinalIgnoreCase))
                    pair.ReferencedColumn = column.Name;
            }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var key in e.OldItems?.OfType<EditableForeignKey>() ?? [])
        {
            key.PropertyChanged -= OnKeyChanged;
            key.PairsChanged -= OnPairsChanged;
        }
        foreach (var key in e.NewItems?.OfType<EditableForeignKey>() ?? [])
        {
            key.PropertyChanged += OnKeyChanged;
            key.PairsChanged += OnPairsChanged;
        }
        if (Selected != null && !Items.Contains(Selected)) Selected = null;
        Raise();
    }

    private void OnKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableForeignKey.Status) or nameof(EditableForeignKey.TargetDisplay)
            or nameof(EditableForeignKey.PairsDisplay)) return;
        if (e.PropertyName == nameof(EditableForeignKey.ReferencedTable) && sender == Selected)
        {
            SyncSelectedTarget();
            RefreshReferencedColumns();
        }
        Raise();
    }

    private void OnPairsChanged() => Raise();

    private void Raise()
    {
        EnsureTargets();
        Changed?.Invoke();
    }
}
