namespace DbConnect.Models;

/// <summary>
/// How a column generates its values, as one value: its identity (kind, seed and increment as integer
/// text, so decimal identities past bigint fit) and its generation (expression and storage). Two columns
/// generate their values the same way exactly when their settings are equal.
/// </summary>
public sealed record ColumnGenerationSettings(
    TableDesignIdentityKind? IdentityKind = null,
    string? Seed = null,
    string? Increment = null,
    string? Expression = null,
    TableDesignGeneratedStorage? Storage = null)
{
    public static ColumnGenerationSettings None { get; } = new();

    /// <summary>The settings of a column as the table design loaded it.</summary>
    public static ColumnGenerationSettings From(TableDesignColumn loaded) =>
        new(loaded.Identity?.Kind, loaded.Identity?.Seed, loaded.Identity?.Increment, loaded.Generated?.Expression, loaded.Generated?.Storage);
}
