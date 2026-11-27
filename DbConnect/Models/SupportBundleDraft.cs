using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Models;

/// <summary>
/// In-memory support-bundle contents that can be inspected before being saved.
/// All entries are already redacted by <see cref="Services.SupportBundleService"/>.
/// </summary>
public sealed class SupportBundleDraft
{
    public SupportBundleDraft(DateTimeOffset createdAt, IReadOnlyList<SupportBundleEntry> entries)
    {
        CreatedAt = createdAt;
        Entries = entries;
    }

    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<SupportBundleEntry> Entries { get; }
    public IReadOnlyList<string> EntryNames => Entries.Select(entry => entry.Name).ToArray();
    public string InspectionText => string.Join(
        Environment.NewLine,
        Entries.Select(entry => $"{entry.Name} ({entry.Content.Length:N0} characters)"));

    public SupportBundleEntry? Find(string name) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
}

public sealed record SupportBundleEntry(string Name, string ContentType, string Content);
