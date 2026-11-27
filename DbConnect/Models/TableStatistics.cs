using System;

namespace DbConnect.Models;

/// <summary>
/// What a provider can tell about a table for the Objects tab and the Information pane. Every value is optional:
/// null means the provider does not supply it, and the UI shows "—".
/// </summary>
public sealed record TableStatistics(string Name)
{
    public string? Schema { get; init; }
    public long? Rows { get; init; }
    public long? DataLength { get; init; }
    public long? IndexLength { get; init; }
    public string? Engine { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Collation { get; init; }
    public long? AutoIncrement { get; init; }
    public string? Comment { get; init; }
}
