using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>One local column and the referenced column it matches, in key order.</summary>
public partial class ForeignKeyPair : ObservableObject
{
    [ObservableProperty] private string _column = "";
    [ObservableProperty] private string _referencedColumn = "";
}

/// <summary>
/// A foreign key as the designer edits it: its ordered column pairs, the referenced table as a
/// structured identity (never a display label), its actions and an optional name (blank on a new key
/// lets the engine name it; blank on a loaded one keeps its name). A loaded key keeps its loaded identity.
/// </summary>
public partial class EditableForeignKey : ObservableObject
{
    public EditableForeignKey(IEnumerable<TableDesignColumnPair>? pairs = null)
    {
        foreach (var pair in pairs ?? []) Pairs.Add(new ForeignKeyPair { Column = pair.Column, ReferencedColumn = pair.ReferencedColumn });
        Pairs.CollectionChanged += (_, e) =>
        {
            foreach (var pair in e.OldItems?.OfType<ForeignKeyPair>() ?? []) pair.PropertyChanged -= OnPairChanged;
            foreach (var pair in e.NewItems?.OfType<ForeignKeyPair>() ?? []) pair.PropertyChanged += OnPairChanged;
            OnPairChanged(null, null!);
        };
        foreach (var pair in Pairs) pair.PropertyChanged += OnPairChanged;
    }

    private void OnPairChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PairsDisplay));
        OnPropertyChanged(nameof(Status));
        PairsChanged?.Invoke();
    }

    /// <summary>Raised when a pair is added, removed, moved or edited.</summary>
    public event System.Action? PairsChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _name;

    public ObservableCollection<ForeignKeyPair> Pairs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(TargetDisplay))]
    private DatabaseObjectIdentity? _referencedTable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _onDelete = "NO ACTION";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _onUpdate = "NO ACTION";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isDropped;

    /// <summary>The key as the table design loaded it; null for one added in the designer.</summary>
    public TableDesignForeignKey? Loaded { get; init; }

    public bool IsNew => Loaded == null;

    public string TargetDisplay => ReferencedTable is { } table ? TableDesignText.Qualified(table) : "";
    public string PairsDisplay => string.Join(", ", Pairs.Select(p => $"{p.Column} -> {p.ReferencedColumn}"));

    /// <summary>The loaded key's MATCH, deferrable or trust state, kept read-only.</summary>
    public string? StateDisplay => Loaded?.State;

    public bool Renamed => Loaded != null && GivenName != null && GivenName != Loaded.Name;

    /// <summary>The pairs, referenced table or actions differ from the loaded key, so it must be re-created.</summary>
    public bool DefinitionChanged => Loaded is { } loaded &&
        (!loaded.Columns.Select(p => (p.Column, p.ReferencedColumn)).SequenceEqual(Pairs.Select(p => (p.Column.Trim(), p.ReferencedColumn.Trim())))
         || !TableIdentity.Same(ReferencedTable, loaded.ReferencedTable)
         || !OnDelete.Equals(loaded.OnDelete, System.StringComparison.OrdinalIgnoreCase)
         || !OnUpdate.Equals(loaded.OnUpdate, System.StringComparison.OrdinalIgnoreCase));

    public string Status => IsDropped ? "Dropped" : IsNew ? "New" : DefinitionChanged ? "Changed" : Renamed ? "Renamed" : "";

    private string? GivenName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    /// <summary>The key as the builders take it; null while it has no referenced table.</summary>
    public TableForeignKeySpec? Spec() => ReferencedTable is not { } table ? null
        : new(GivenName ?? Loaded?.Name, Pairs.Select(p => new TableDesignColumnPair(p.Column.Trim(), p.ReferencedColumn.Trim())).ToList(),
            table, OnDelete, OnUpdate);
}

/// <summary>A foreign key to create, drop or rename. <see cref="Name"/> is null when the engine names it.</summary>
public sealed record TableForeignKeySpec(
    string? Name,
    IReadOnlyList<TableDesignColumnPair> Pairs,
    DatabaseObjectIdentity ReferencedTable,
    string OnDelete,
    string OnUpdate)
{
    /// <summary>The name the engine gave a loaded key it named itself (SQL Server system names).</summary>
    public string? GeneratedName { get; init; }

    /// <summary>The name a statement addresses the loaded key by.</summary>
    public string Address => Name ?? GeneratedName
        ?? throw new System.InvalidOperationException("The foreign key's name is not known; reload the table.");

    public IEnumerable<string> Columns => Pairs.Select(p => p.Column);
    public IEnumerable<string> ReferencedColumns => Pairs.Select(p => p.ReferencedColumn);
}

/// <summary>One change to a loaded foreign key, or a new one: no From for an added key, no To for a dropped one.</summary>
public sealed record TableForeignKeyChange(TableForeignKeySpec? From, TableForeignKeySpec? To);

