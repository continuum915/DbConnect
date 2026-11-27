using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Adapts existing schema roles to immutable catalog records using effective session settings.</summary>
internal sealed class ProviderMetadataCatalogSource(DatabaseSession session) : IMetadataCatalogSource
{
    public async Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken)
    {
        using var context = new MetadataReadContext(cancellationToken);
        if (scope.Parent != null) session.Objects.Validate(scope.Parent);
        if (scope == MetadataScope.Root || scope.Kind is SchemaNodeType.Schema or SchemaNodeType.Database)
        {
            var roots = await session.Provider.GetSchemaAsync(session.ConnectionString).WaitAsync(cancellationToken);
            await session.Objects.LoadNamesAsync(cancellationToken).WaitAsync(cancellationToken);
            foreach (var root in roots) session.Objects.Bind(root, null);
            if (scope == MetadataScope.Root) return roots.Select(Capture).ToImmutableArray();
            var owner = roots.SelectMany(Flatten).FirstOrDefault(node => node.Identity == scope.Parent);
            return owner?.Children.Select(Capture).ToImmutableArray() ?? [];
        }
        var parent = scope.Parent!;
        if (scope.Kind == SchemaNodeType.Schema)
        {
            var roots = await session.Provider.GetSchemaAsync(session.ConnectionString).WaitAsync(cancellationToken);
            foreach (var root in roots) session.Objects.Bind(root, null);
            return roots.SelectMany(root => root.Children)
                .Where(node => session.Objects.Same(node.Identity, parent))
                .SelectMany(node => node.Children).Select(Capture).ToImmutableArray();
        }
        if (scope.Kind == SchemaNodeType.Column)
        {
            var columns = await ((IStructuredSchemaProvider)session.Provider).GetColumnsAsync(session.ConnectionString, parent).WaitAsync(cancellationToken);
            return columns.Select(column => new MetadataRecord(new(parent.Catalog, parent.Schema, column.Name, SchemaNodeType.Column)
            {
                Parent = parent,
                ConnectionId = parent.ConnectionId,
                Generation = parent.Generation
            }, $"{column.Name} ({column.DataType})")
            {
                DataType = column.DataType,
                Description = column.Comment,
                IsPrimaryKey = column.IsPrimaryKey
            }).ToImmutableArray();
        }
        var nodes = await ((IStructuredSchemaProvider)session.Provider).GetChildrenAsync(session.ConnectionString, parent, scope.Kind).WaitAsync(cancellationToken);
        foreach (var node in nodes) session.Objects.Bind(node, parent);
        return nodes.Select(Capture).ToImmutableArray();
    }

    private static System.Collections.Generic.IEnumerable<SchemaNode> Flatten(SchemaNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child)) yield return descendant;
    }
    private static MetadataRecord Capture(SchemaNode node) => new(node.Identity!, node.Name)
    {
        Children = node.Children.Where(child => child.Name != "Loading…").Select(Capture).ToImmutableArray(),
        DataType = node.DataType,
        Description = node.Description,
        IsPrimaryKey = node.IsPrimaryKey,
        IsLazyLoadable = node.IsLazyLoadable,
        IsLoaded = node.IsLoaded,
        ChildCount = node.ChildCount,
        SchemaPath = node.SchemaPath
    };

    public static SchemaNode Project(MetadataRecord record) => new()
    {
        Name = record.DisplayName,
        RawName = record.Identity.Name,
        Identity = record.Identity,
        Type = record.Identity.Kind,
        CatalogName = record.Identity.Catalog,
        SchemaName = record.Identity.Schema,
        SchemaPath = record.SchemaPath,
        RoutineSignature = record.Identity.RoutineSignature,
        DataType = record.DataType,
        Description = record.Description,
        IsPrimaryKey = record.IsPrimaryKey,
        IsLazyLoadable = record.IsLazyLoadable,
        IsLoaded = record.IsLoaded,
        ChildCount = record.ChildCount,
        MetadataState = record.IsLazyLoadable && !record.IsLoaded ? MetadataLoadState.NotLoaded :
            record.Identity.Kind == SchemaNodeType.Database && record.Children.IsEmpty ? MetadataLoadState.LoadedEmpty : MetadataLoadState.Loaded,
        Children = record.IsLazyLoadable && !record.IsLoaded && record.Children.IsEmpty
            ? new([new SchemaNode { Name = "Expand to load", Type = SchemaNodeType.Column }])
            : new(record.Children.Select(Project))
    };
}
