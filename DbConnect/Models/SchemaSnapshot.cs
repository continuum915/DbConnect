using System;

namespace DbConnect.Models;

public class SchemaSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ConnectionName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Label { get; set; } = string.Empty;
    public int ObjectCount { get; set; }
    public string SchemaJson { get; set; } = string.Empty;
}
