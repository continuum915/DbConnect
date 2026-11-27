using System.Collections.Immutable;

namespace DbConnect.Models;

public enum MetadataLoadState { NotLoaded, Loading, LoadedEmpty, Loaded, Failed, Stale, Pending }

public sealed record MetadataScope(DatabaseObjectIdentity? Parent, SchemaNodeType Kind)
{
    public static MetadataScope Root { get; } = new(null, SchemaNodeType.Database);

    public static MetadataScope ListingFor(DatabaseObjectIdentity identity) => identity.Parent is not { } parent
        ? Root : new(parent, parent.Kind == SchemaNodeType.Database ? SchemaNodeType.Database : identity.Kind);
    /// <summary>Includes only this listing and metadata owned by the objects it lists.</summary>
    public bool Contains(MetadataScope scope)
    {
        if (this == Root || this == scope) return true;
        for (var owner = scope.Parent; owner != null; owner = owner.Parent)
        {
            if (Kind is SchemaNodeType.Schema or SchemaNodeType.Database or SchemaNodeType.Column && owner == Parent)
                return true;
            if (owner.Kind == Kind && owner.Parent == Parent) return true;
        }
        return false;
    }
}

/// <summary>Immutable metadata, independent of observable explorer nodes.</summary>
public sealed record MetadataRecord(DatabaseObjectIdentity Identity, string DisplayName)
{
    public ImmutableArray<MetadataRecord> Children { get; init; } = [];
    public string? DataType { get; init; }
    public string? Description { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsLazyLoadable { get; init; }
    public bool IsLoaded { get; init; }
    public int? ChildCount { get; init; }
    public string? SchemaPath { get; init; }
}

public sealed record MetadataSnapshot(MetadataLoadState State, long Revision,
    ImmutableArray<MetadataRecord> Records, bool HasSnapshot = false, string? Error = null);
