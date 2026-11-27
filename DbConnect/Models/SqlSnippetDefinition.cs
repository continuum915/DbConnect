namespace DbConnect.Models;

public sealed class SqlSnippetDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;

    public string DisplayName => $"{Name} - {Description}";
}
