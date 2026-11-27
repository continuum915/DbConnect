using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Reconciles catalog records by address, retaining expanded and selected node instances.</summary>
internal static class ExplorerCatalogProjection
{
    public static void Apply(SchemaNode owner, ImmutableArray<MetadataRecord> records, MetadataCatalog catalog)
    {
        var children = new List<SchemaNode>();
        foreach (var record in records)
        {
            var node = owner.Children.FirstOrDefault(child => child.Identity == record.Identity)
                ?? ProviderMetadataCatalogSource.Project(record);
            node.Name = record.DisplayName;
            node.DataType = record.DataType;
            node.Description = record.Description;
            node.IsPrimaryKey = record.IsPrimaryKey;
            var scope = Scope(node);
            var snapshot = scope == null ? null : catalog.GetSnapshot(scope);
            if (snapshot != null && snapshot.State != MetadataLoadState.NotLoaded)
            {
                node.MetadataState = snapshot.State;
                node.MetadataError = snapshot.Error;
                if (snapshot.State is MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)
                {
                    var details = snapshot.Records;
                    if (scope!.Kind == SchemaNodeType.Column)
                        details = details.AddRange(record.Children.Where(child => child.IsLazyLoadable));
                    Apply(node, details, catalog);
                    node.IsLoaded = true;
                }
            }
            else if (!node.IsLazyLoadable || record.IsLoaded)
                Apply(node, record.Children, catalog);
            children.Add(node);
        }
        owner.Children = new ObservableCollection<SchemaNode>(children);
        owner.ChildCount = children.Count;
    }

    public static SchemaNode? FindListingOwner(SchemaNode node, DatabaseObjectIdentity identity)
    {
        if (node.Identity == identity.Parent && node.Type == SchemaNodeType.Database) return node;
        if (node.Identity?.Parent == identity.Parent && ExplorerCatalogLoader.ChildKind(node.Type) == identity.Kind) return node;
        foreach (var child in node.Children)
            if (FindListingOwner(child, identity) is { } owner) return owner;
        return null;
    }
    private static MetadataScope? Scope(SchemaNode node) => node.Identity is not { } identity ? null : node.Type switch
    {
        SchemaNodeType.Schema => new(identity, SchemaNodeType.Schema),
        SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView => new(identity, SchemaNodeType.Column),
        _ => node.IsLazyLoadable ? new(identity.Parent ?? identity, ExplorerCatalogLoader.ChildKind(node.Type)) : null
    };
}
