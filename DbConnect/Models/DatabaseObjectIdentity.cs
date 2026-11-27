using System;

namespace DbConnect.Models;

/// <summary>Names as returned by the catalog, without SQL quoting or dotted-path parsing.</summary>
public sealed record DatabaseObjectIdentity(string? Catalog, string? Schema, string Name, SchemaNodeType Kind)
{
    public Guid ConnectionId { get; init; }
    public Guid Generation { get; init; }
    public DatabaseObjectIdentity? Parent { get; init; }
    public string? RoutineSignature { get; init; }
}
