using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public static class SchemaChangeScopes
{
    public static MetadataScope Routine(DatabaseSession session, string schema, SchemaNodeType kind)
    {
        var owner = session.Catalog.GetSnapshot(MetadataScope.Root).Records.SelectMany(root => root.Children)
            .FirstOrDefault(record => record.Identity.Kind == SchemaNodeType.Schema && record.Identity.Name == schema);
        return owner == null ? MetadataScope.Root : new(owner.Identity, kind);
    }

    public static MetadataScope ObjectDetails(DatabaseSession session, SchemaNode? node)
    {
        node = node?.RealNode ?? node;
        if (node == null) return MetadataScope.Root;
        var identity = node.Identity ?? session.Objects.Bind(node, null);
        return CatalogSqlCompletion.IsRelation(identity.Kind) ? new(identity, SchemaNodeType.Column) : Membership(session, node);
    }

    public static MetadataScope Membership(DatabaseSession session, SchemaNode node)
    {
        node = node.RealNode ?? node;
        var identity = node.Identity ?? session.Objects.Bind(node, null);
        return identity.Parent?.Kind == SchemaNodeType.Schema
            ? new(identity.Parent, identity.Kind) : MetadataScope.Root;
    }
}
