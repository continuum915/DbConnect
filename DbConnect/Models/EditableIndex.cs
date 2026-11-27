using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>
/// One key part of an index as the designer edits it: a column, or an expression (loaded or typed), and its
/// direction. A loaded part keeps its collation and further settings, which are shown and preserved but not edited.
/// </summary>
public partial class EditableIndexPart : ObservableObject
{
    public EditableIndexPart(string? column, string? expression = null, bool descending = false, TableDesignIndexPart? loaded = null)
    {
        _column = column;
        _expression = expression;
        _descending = descending;
        Loaded = loaded;
    }

    /// <summary>The column; null for an expression part.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display), nameof(KeyText))]
    private string? _column;

    /// <summary>The expression, as typed or loaded; unused for a column part.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display), nameof(KeyText))]
    private string? _expression;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private bool _descending;

    /// <summary>The part as the table design loaded it; null for a part added in the designer.</summary>
    public TableDesignIndexPart? Loaded { get; }

    public bool IsExpression => Column == null;

    /// <summary>The column or the parenthesised expression.</summary>
    public string KeyText => Column ?? $"({Expression})";

    public string Display => TableDesignText.Describe(Part());

    /// <summary>The same column or expression (ignoring case and surrounding spaces) in the same direction.</summary>
    public bool SameKey(EditableIndexPart other) =>
        string.Equals(KeyText.Trim(), other.KeyText.Trim(), StringComparison.OrdinalIgnoreCase) && Descending == other.Descending;

    /// <summary>The part as loaded, with only its direction and expression edited: a renamed column is the same column.</summary>
    public TableDesignIndexPart AsLoaded() => Loaded is { } loaded ? Part() with { Column = loaded.Column } : Part();

    /// <summary>
    /// The part as the builders take it, with its loaded collation and settings. An expression is trimmed once
    /// edited; an unedited one keeps the engine's text.
    /// </summary>
    public TableDesignIndexPart Part() =>
        new(Column, Column == null ? ExpressionText : null, Descending) { Collation = Loaded?.Collation, Options = Loaded?.Options };

    private string? ExpressionText => Expression == Loaded?.Expression ? Expression : Expression?.Trim();
}

/// <summary>
/// An index as the designer edits it: its name, uniqueness, ordered key parts, included columns, filter and
/// method. A loaded index keeps its identity and the properties the designer does not edit (engine settings,
/// comment), which block re-creating it. An index that backs a primary or unique key is shown read-only; the
/// key owns it.
/// </summary>
public partial class EditableIndex : ObservableObject
{
    public EditableIndex(TableDesignIndex? loaded = null, string? storedSql = null, string? method = null)
    {
        Loaded = loaded;
        StoredSql = storedSql;
        Parts.CollectionChanged += OnPartsChanged;
        IncludedColumns.CollectionChanged += OnIncludedChanged;
        _method = method;
        if (loaded != null)
        {
            _name = loaded.Name;
            _isUnique = loaded.IsUnique;
            _filter = loaded.Filter ?? "";
            _method = loaded.Method;
            foreach (var part in loaded.Parts) Parts.Add(new EditableIndexPart(part.Column, part.Expression, part.Descending, part));
            foreach (var column in loaded.IncludedColumns) IncludedColumns.Add(column);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isUnique;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isDropped;

    /// <summary>The filter (WHERE predicate) as edited; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(FilterDisplay))]
    private string _filter = "";

    /// <summary>The index method (USING, or SQL Server's CLUSTERED/NONCLUSTERED); null for the engine's default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(MethodDisplay))]
    private string? _method;

    /// <summary>The key parts in index order.</summary>
    public ObservableCollection<EditableIndexPart> Parts { get; } = new();

    /// <summary>The included (non-key) columns in order.</summary>
    public ObservableCollection<string> IncludedColumns { get; } = new();

    /// <summary>The index as the table design loaded it; null for an index added in the designer.</summary>
    public TableDesignIndex? Loaded { get; }

    /// <summary>The engine's own CREATE INDEX text, which a SQLite rebuild replays for an unchanged index.</summary>
    public string? StoredSql { get; }

    public bool IsNew => Loaded == null;

    /// <summary>The index backs a primary or unique key, which owns it.</summary>
    public bool IsReadOnly => Loaded != null && Loaded.Origin != TableDesignIndexOrigin.CreateIndex;

    public bool IsEditable => !IsReadOnly;

    public string PartsDisplay => string.Join(", ", Parts.Select(p => p.Display));
    public string? IncludedDisplay => IncludedColumns.Count > 0 ? string.Join(", ", IncludedColumns) : null;
    public string? FilterDisplay => FilterText;
    public string? MethodDisplay => Method;
    public string? CommentDisplay => Loaded?.Comment;
    public string? PropertiesDisplay => Loaded is { Properties.Count: > 0 } loaded ? string.Join("; ", loaded.Properties) : null;

    public string BacksDisplay => Loaded?.Origin switch
    {
        TableDesignIndexOrigin.PrimaryKey => "Primary key",
        TableDesignIndexOrigin.UniqueKey => "Unique key",
        _ => ""
    };

    /// <summary>The name as given, trimmed.</summary>
    public string GivenName => Name.Trim();

    public bool Renamed => Loaded != null && GivenName.Length > 0 && GivenName != Loaded.Name;

    /// <summary>The filter as the builders take it: the engine's text while unedited, trimmed once edited, null when empty.</summary>
    public string? FilterText => Loaded != null && Filter == (Loaded.Filter ?? "") ? Loaded.Filter
        : string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim();

    /// <summary>The filter, included columns or method differ from the loaded ones. A renamed included column is the same column.</summary>
    public bool SettingsEdited =>
        Loaded is { } loaded && (FilterText != loaded.Filter || !string.Equals(Method, loaded.Method, StringComparison.OrdinalIgnoreCase)
                                 || !IncludedColumns.Select(c => _renamedIncluded.GetValueOrDefault(c, c)).SequenceEqual(loaded.IncludedColumns));

    /// <summary>A column was renamed: an included column follows it, as the engine's would, and still counts as the loaded one.</summary>
    public void RenameIncluded(string old, string name)
    {
        for (var i = 0; i < IncludedColumns.Count; i++)
        {
            if (!old.Equals(IncludedColumns[i], StringComparison.OrdinalIgnoreCase)) continue;
            _renamedIncluded[name] = _renamedIncluded.GetValueOrDefault(IncludedColumns[i], IncludedColumns[i]);
            IncludedColumns[i] = name;
        }
    }

    private readonly Dictionary<string, string> _renamedIncluded = new(StringComparer.OrdinalIgnoreCase);

    public string Status => IsReadOnly ? "" : IsDropped ? "Dropped" : IsNew ? "New"
        : IsUnique != Loaded!.IsUnique || !Parts.Select(p => p.AsLoaded()).SequenceEqual(Loaded.Parts) || SettingsEdited ? "Changed"
        : Renamed ? "Renamed" : "";

    /// <summary>The trimmed columns of the column key parts, in order.</summary>
    public IEnumerable<string> KeyColumns => Parts.Where(p => !p.IsExpression).Select(p => p.Column!.Trim());

    /// <summary>
    /// The same uniqueness, keys in the same order and directions, included columns, filter and method (ignoring
    /// case), so one of the two indexes is redundant. No method (a new index where the engine offers none) matches any.
    /// </summary>
    public bool SameDefinition(EditableIndex other) =>
        IsUnique == other.IsUnique && Parts.Count == other.Parts.Count && Parts.Zip(other.Parts).All(pair => pair.First.SameKey(pair.Second))
        && IncludedColumns.SequenceEqual(other.IncludedColumns, StringComparer.OrdinalIgnoreCase)
        && string.Equals(FilterText, other.FilterText, StringComparison.OrdinalIgnoreCase)
        && (Method == null || other.Method == null || string.Equals(Method, other.Method, StringComparison.OrdinalIgnoreCase));

    /// <summary>The index as the builders take it: its name, uniqueness, parts, included columns, filter and method.</summary>
    public TableDesignIndex Spec() => new(GivenName, IsUnique, Parts.Select(p => p.Part()).ToList(), TableDesignIndexOrigin.CreateIndex)
    {
        IncludedColumns = IncludedColumns.ToList(),
        Filter = FilterText,
        Method = Method
    };

    private void OnPartsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var part in e.OldItems?.OfType<EditableIndexPart>() ?? []) part.PropertyChanged -= OnPartChanged;
        foreach (var part in e.NewItems?.OfType<EditableIndexPart>() ?? []) part.PropertyChanged += OnPartChanged;
        PartsEdited();
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableIndexPart.Column) or nameof(EditableIndexPart.Expression) or nameof(EditableIndexPart.Descending))
            PartsEdited();
    }

    private void PartsEdited()
    {
        OnPropertyChanged(nameof(PartsDisplay));
        OnPropertyChanged(nameof(Status));
    }

    private void OnIncludedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IncludedDisplay));
        OnPropertyChanged(nameof(Status));
    }
}

/// <summary>One change to a loaded index, or a new one: <see cref="From"/> is null for an added index, <see cref="To"/> for a dropped one.</summary>
public sealed record TableIndexChange(TableDesignIndex? From, TableDesignIndex? To);
