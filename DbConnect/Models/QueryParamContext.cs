using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public class QueryParamContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SqlFingerprint { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string? ConnectionName { get; set; }
    public string? DatabaseName { get; set; }
    public List<QueryParamEntry> Parameters { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}
