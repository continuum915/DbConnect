using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Reuses surviving nodes so expansion, selection and open object references retain their address.</summary>
internal static class ExplorerMetadataProjection
{
    public static ObservableCollection<SchemaNode> Reconcile(IEnumerable<SchemaNode> previous,
        ImmutableArray<MetadataRecord> records, DatabaseSession session)
    {
        var existing = previous.ToList();
        return new(records.Select(record =>
        {
            var node = existing.FirstOrDefault(node => session.Objects.Same(node.Identity, record.Identity));
            if (node == null)
            {
                node = ProviderMetadataCatalogSource.Project(record);
                ApplyColumns(node, session);
                return node;
            }
            node.Name = record.DisplayName;
            node.RawName = record.Identity.Name;
            node.DataType = record.DataType;
            node.Description = record.Description;
            node.IsPrimaryKey = record.IsPrimaryKey;
            if (node.MetadataState == MetadataLoadState.Pending)
            {
                node.MetadataState = record.IsLazyLoadable && !node.IsLoaded ? MetadataLoadState.NotLoaded : MetadataLoadState.Loaded;
                node.MetadataError = null;
            }
            if (!record.IsLazyLoadable || record.IsLoaded)
            {
                node.Children = Reconcile(node.Children, record.Children, session);
                node.ChildCount = record.ChildCount;
            }
            ApplyColumns(node, session);
            return node;
        }));
    }

    public static void ApplyColumns(SchemaNode node, DatabaseSession session)
    {
        if (!CatalogSqlCompletion.IsRelation(node.Type) || node.Identity == null) return;
        var snapshot = session.Catalog.GetSnapshot(new(node.Identity, SchemaNodeType.Column));
        if (!snapshot.HasSnapshot) return;
        node.Children = new(snapshot.Records.Select(ProviderMetadataCatalogSource.Project)
            .Concat(node.Children.Where(child => child.IsLazyLoadable)));
        node.IsLoaded = true;
        node.MetadataState = snapshot.State;
        node.MetadataError = snapshot.Error;
    }

    public static MetadataScope Scope(SchemaNode node) => node.Type == SchemaNodeType.Schema
        ? new(node.Identity, SchemaNodeType.Schema)
        : new(ExplorerCatalogLoader.ChildKind(node.Type) == SchemaNodeType.Column
            ? node.Identity : node.Identity!.Parent ?? node.Identity, ExplorerCatalogLoader.ChildKind(node.Type));
}
