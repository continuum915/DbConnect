using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

public enum KeyKind { Primary, Unique }

/// <summary>
/// A primary or unique key as the designer edits it: its ordered member columns and an optional
/// name (blank lets the engine name it). A key loaded from the table keeps its loaded identity,
/// including the backing index whose real name addresses a key the engine named itself.
/// </summary>
public partial class KeyDefinition : ObservableObject
{
    public KeyDefinition(KeyKind kind, IEnumerable<string>? columns = null)
    {
        Kind = kind;
        foreach (var column in columns ?? []) Columns.Add(column);
        Columns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ColumnsDisplay));
            OnPropertyChanged(nameof(Status));
        };
    }

    public KeyKind Kind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _name;

    /// <summary>The member columns in key order.</summary>
    public ObservableCollection<string> Columns { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isDropped;

    public bool IsNew => Loaded == null;

    /// <summary>The key as the table design loaded it; null for a key added in the designer.</summary>
    public TableDesignKey? Loaded { get; init; }

    /// <summary>The engine fixes this key's name (MySQL's primary key is always PRIMARY).</summary>
    public bool NameLocked { get; init; }

    /// <summary>The index that enforces the loaded key, when the engine reports one.</summary>
    public TableDesignIndex? BackingIndex { get; init; }

    public string KindDisplay => Kind == KeyKind.Primary ? "Primary key" : "Unique";
    public string ColumnsDisplay => string.Join(", ", Columns);

    public bool MembersChanged => Loaded != null && !Loaded.Columns.SequenceEqual(Columns);

    /// <summary>A loaded key was given a new name. A blank name keeps the loaded one: only new keys are left for the engine to name.</summary>
    public bool Renamed => Loaded != null && GivenName != null && GivenName != Loaded.Name;

    private string? GivenName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    public string Status => IsDropped ? "Dropped" : IsNew ? "New" : MembersChanged ? "Changed" : Renamed ? "Renamed" : "";

    /// <summary>The key as the builders take it.</summary>
    public TableKeySpec Spec() => new(Kind, GivenName ?? Loaded?.Name, Columns.ToList());
}

/// <summary>A key to create, drop or rename: its kind, its name (null when the engine names it) and its ordered columns.</summary>
public sealed record TableKeySpec(KeyKind Kind, string? Name, IReadOnlyList<string> Columns)
{
    /// <summary>The engine's index kind to restate (SQL Server CLUSTERED or NONCLUSTERED); null for the default.</summary>
    public string? Method { get; init; }

    /// <summary>The loaded key's backing index; SQL Server and MySQL address a key they named themselves by it.</summary>
    public string? IndexName { get; init; }

    /// <summary>The name a statement addresses the loaded key by: its own, or its backing index's.</summary>
    public string Address => Name ?? IndexName
        ?? throw new System.InvalidOperationException("The key's name is not known; reload the table.");
}

/// <summary>One change to a loaded key, or a new one: <see cref="From"/> is null for an added key, <see cref="To"/> for a dropped one.</summary>
public sealed record TableKeyChange(TableKeySpec? From, TableKeySpec? To);
