using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>
/// A check constraint as the designer edits it: its expression as SQL text and an optional name (blank on
/// a new check lets the engine name it; blank on a loaded one keeps its name). A loaded check keeps its
/// loaded identity, its column when declared on one, and its enforcement state, which stays read-only.
/// </summary>
public partial class EditableCheck : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _expression = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isDropped;

    /// <summary>The check as the table design loaded it; null for one added in the designer.</summary>
    public TableDesignCheck? Loaded { get; init; }

    public bool IsNew => Loaded == null;

    /// <summary>The column a loaded column-level check is declared on.</summary>
    public string? Column => Loaded?.Column;

    /// <summary>The loaded check's NOT VALID, NOT ENFORCED, disabled or untrusted state, kept read-only.</summary>
    public string? StateDisplay => Loaded?.State;

    public bool ExpressionChanged => Loaded != null && Expression.Trim() != Loaded.Expression;
    public bool Renamed => Loaded != null && GivenName != null && GivenName != Loaded.Name;

    public string Status => IsDropped ? "Dropped" : IsNew ? "New" : ExpressionChanged ? "Changed" : Renamed ? "Renamed" : "";

    private string? GivenName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    /// <summary>The loaded check as the builders address it; null for a new one.</summary>
    public TableCheckSpec? LoadedSpec() => Loaded is not { } loaded ? null
        : new(loaded.Name, loaded.Expression) { Column = loaded.Column, GeneratedName = loaded.GeneratedName };

    /// <summary>The check as the builders take it.</summary>
    public TableCheckSpec Spec() => new(GivenName ?? Loaded?.Name, Expression.Trim());
}

/// <summary>A check to create, drop or rename. <see cref="Name"/> is null when the engine names it.</summary>
public sealed record TableCheckSpec(string? Name, string Expression)
{
    /// <summary>The column a loaded check is declared on, for finding it in a stored SQLite definition.</summary>
    public string? Column { get; init; }

    /// <summary>The name the engine gave a loaded check it named itself (SQL Server system names).</summary>
    public string? GeneratedName { get; init; }

    public string Address => Name ?? GeneratedName
        ?? throw new System.InvalidOperationException("The check's name is not known; reload the table.");
}

/// <summary>One change to a loaded check, or a new one: no From for an added check, no To for a dropped one.</summary>
public sealed record TableCheckChange(TableCheckSpec? From, TableCheckSpec? To);

/// <summary>The keys, foreign keys and checks CREATE TABLE declares.</summary>
public sealed record TableConstraints(
    IReadOnlyList<TableKeySpec> Keys,
    IReadOnlyList<TableForeignKeySpec> ForeignKeys,
    IReadOnlyList<TableCheckSpec> Checks);
