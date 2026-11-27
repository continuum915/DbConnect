using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.Models;

/// <summary>A column as the designer edits it. Edits raise PropertyChanged so the planned SQL can follow them.</summary>
public partial class ColumnDefinition : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _dataType = "TEXT";
    [ObservableProperty] private bool _isNullable = true;
    [ObservableProperty] private bool _isPrimaryKey;
    [ObservableProperty] private string? _defaultValue;
    [ObservableProperty] private int? _maxLength;      // catalog byte/character length as metadata reports it; the designer edits Length
    /// <summary>The type's length modifier, e.g. 40 or max; <see cref="DataType"/> is then the type name without it.</summary>
    [ObservableProperty] private string? _length;
    [ObservableProperty] private int? _precision;
    [ObservableProperty] private int? _scale;
    /// <summary>The column's collation as SQL text (quoted as the engine needs); null for the type's default.</summary>
    [ObservableProperty] private string? _collation;
    /// <summary>How the column generates its values; null when it does not. <see cref="IsIdentity"/> follows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdentity), nameof(IdentityLabel))]
    private TableDesignIdentityKind? _identityKind;
    /// <summary>The identity's start value as integer text, where the engine has one per column.</summary>
    [ObservableProperty] private string? _identitySeed;
    [ObservableProperty] private string? _identityIncrement;
    /// <summary>The generated (computed) column's expression as SQL text; null for an ordinary column.</summary>
    [ObservableProperty] private string? _generatedExpression;
    /// <summary>Whether a generated column's values are stored; null for the engine's default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeneratedStorageLabel))]
    private TableDesignGeneratedStorage? _generatedStorage;
    [ObservableProperty] private bool _isNew;          // true = added in designer session
    [ObservableProperty] private bool _isDropped;      // true = scheduled for DROP
    [ObservableProperty] private string? _comment;     // optional column-level comment (PostgreSQL, MySQL, SQL Server)
    public ColumnDefinition? Original { get; set; } // snapshot before edit

    /// <summary>The column as the loaded table design has it; its engine properties and generator options are shown read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeneratorDisplay), nameof(PropertiesDisplay))]
    private TableDesignColumn? _loaded;

    public bool IsIdentity => IdentityKind != null;

    /// <summary>The identity and generation fields as one value.</summary>
    public ColumnGenerationSettings Generation
    {
        get => new(IdentityKind, IdentitySeed, IdentityIncrement, GeneratedExpression, GeneratedStorage);
        set
        {
            IdentityKind = value.IdentityKind;
            IdentitySeed = value.Seed;
            IdentityIncrement = value.Increment;
            GeneratedExpression = value.Expression;
            GeneratedStorage = value.Storage;
        }
    }

    /// <summary>The identity kind as the designer's Identity cell shows and edits it; empty for none.</summary>
    public string IdentityLabel
    {
        get => ColumnGenerationLabels.Of(IdentityKind);
        set => IdentityKind = ColumnGenerationLabels.IdentityKind(value);
    }

    /// <summary>The storage as the designer's Storage cell shows and edits it; empty for the engine's default.</summary>
    public string GeneratedStorageLabel
    {
        get => GeneratedStorage?.ToString() ?? "";
        set => GeneratedStorage = Enum.TryParse<TableDesignGeneratedStorage>(value, true, out var storage) ? storage : null;
    }

    /// <summary>
    /// What the loaded column's generator has beyond the editable fields: identity options such as
    /// MINVALUE or NOT FOR REPLICATION, and the sequence its default draws from (owned or external).
    /// </summary>
    public string? GeneratorDisplay => Loaded is not { } loaded ? null
        : string.Join("; ", new[]
        {
            loaded.Identity?.Options,
            loaded.Sequence is { } sequence ? TableDesignText.Describe(sequence) : null
        }.Where(s => s != null)) is { Length: > 0 } text ? text : null;
    public string? PropertiesDisplay => Loaded is { Properties.Count: > 0 } loaded ? string.Join("; ", loaded.Properties) : null;

    // Choosing no identity clears its seed and increment, which have no meaning without it.
    partial void OnIdentityKindChanged(TableDesignIdentityKind? value)
    {
        if (value != null) return;
        IdentitySeed = null;
        IdentityIncrement = null;
    }

    /// <summary>Creates a deep copy of the definition.</summary>
    public ColumnDefinition Clone() => new()
    {
        Name = Name,
        DataType = DataType,
        IsNullable = IsNullable,
        IsPrimaryKey = IsPrimaryKey,
        Generation = Generation,
        DefaultValue = DefaultValue,
        MaxLength = MaxLength,
        Length = Length,
        Precision = Precision,
        Scale = Scale,
        Collation = Collation,
        IsNew = IsNew,
        IsDropped = IsDropped,
        Comment = Comment,
        Original = Original,
        Loaded = Loaded
    };
}
