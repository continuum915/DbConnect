using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public enum SnippetScope { Global, Connection, Database }

public class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public SnippetScope Scope { get; set; } = SnippetScope.Global;
    public string? ConnectionName { get; set; }
    public string? DatabaseName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}
