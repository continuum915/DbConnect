using System;

namespace DbConnect.Models;

public sealed class SavedQueryDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ProviderName)
            ? Name
            : $"{Name} ({ProviderName})";
}
