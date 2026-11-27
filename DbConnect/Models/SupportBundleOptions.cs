using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>Optional, sanitized context included in a support-bundle draft.</summary>
public sealed class SupportBundleOptions
{
    public string? ApplicationVersion { get; init; }
    public IReadOnlyDictionary<string, string?> SettingsSummary { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyList<DatabaseSessionSummary> Sessions { get; init; } = [];
    public int MaxDiagnosticEvents { get; init; } = 1000;
}

/// <summary>Safe session metadata; never contains a connection string or credentials.</summary>
public sealed record DatabaseSessionSummary(
    string Name,
    string Provider,
    string EnvironmentTier,
    string? Database);
