using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Connection-scoped discovery and metadata access for explorer objects.</summary>
public sealed class ObjectNavigation(DatabaseSession session)
{
    public Guid Generation { get; } = Guid.NewGuid();
    private List<SchemaNode> _roots = [];
    private readonly ObjectNameSemantics _names = new(session);

    public async Task<List<SchemaNode>> GetSchemaAsync()
    {
        var snapshot = await session.Catalog.LoadAsync(MetadataScope.Root);
        if (!snapshot.HasSnapshot) throw new InvalidOperationException(snapshot.Error ?? "Metadata is unavailable.");
        var roots = snapshot.Records.Select(ProviderMetadataCatalogSource.Project).ToList();
        _roots = roots;
        return roots;
    }

    internal Task LoadNamesAsync(System.Threading.CancellationToken cancellationToken) => _names.LoadAsync(cancellationToken);

    public DatabaseObjectIdentity Bind(SchemaNode node, DatabaseObjectIdentity? parent)
    {
        var schema = node.Type == SchemaNodeType.Schema ? node.RawName ?? node.Name
            : node.SchemaName ?? node.SchemaPath ?? parent?.Schema;
        // Folder SchemaPath is a legacy display/load hint and can contain a flattened table path.
        if (node.IsLazyLoadable) schema = parent?.Schema;
        var identity = new DatabaseObjectIdentity(node.CatalogName ?? parent?.Catalog, schema,
            node.RawName ?? node.Name, node.Type)
        {
            ConnectionId = session.Connection.Id,
            Generation = Generation,
            Parent = parent,
            RoutineSignature = node.RoutineSignature
        };
        node.Identity = identity;
        foreach (var child in node.Children) Bind(child, identity);
        return identity;
    }

    public DatabaseObjectIdentity Rebind(DatabaseObjectIdentity identity) => identity with
    {
        ConnectionId = session.Connection.Id,
        Generation = Generation,
        Parent = identity.Parent is null ? null : Rebind(identity.Parent)
    };

    public bool Same(DatabaseObjectIdentity? left, DatabaseObjectIdentity? right) =>
        left != null && right != null && left.ConnectionId == right.ConnectionId && left.Generation == right.Generation &&
        left.Kind == right.Kind && NameEquals(left.Catalog, right.Catalog) &&
        NameEquals(left.Schema, right.Schema) && NameEquals(left.Name, right.Name) &&
        left.RoutineSignature == right.RoutineSignature &&
        (left.Kind is SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView ||
         (left.Parent is null && right.Parent is null) || Same(left.Parent, right.Parent));

    private bool NameEquals(string? left, string? right) => _names.Equal(left, right);

    internal bool MatchesSqlName(string stored, string token) => _names.Equal(stored, SqlName(token));

    internal string SqlName(string token)
    {
        var quoted = token.Length > 1 && token[0] is '"' or '`' or '[';
        var name = SqlCompletionNames.Decode(token);
        if (!quoted && session.Capabilities.BatchDialect == SqlBatchDialect.PostgreSql)
            name = string.Concat(name.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c));
        return name;
    }

    public async Task<DatabaseObjectIdentity?> ResolveLegacyRelationAsync(string name)
    {
        var roots = await GetSchemaAsync();
        var relations = new List<DatabaseObjectIdentity>();
        foreach (var root in roots) await CollectRelationsAsync(root, relations);
        // Both literal and flattened historical interpretations are candidates. Never split a raw name.
        var matches = relations.Where(relation => NameEquals(relation.Name, name) ||
            (relation.Schema != null && NameEquals(relation.Schema + "." + relation.Name, name)) ||
            (relation.Catalog != null && NameEquals(relation.Catalog + "." + relation.Name, name))).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task CollectRelationsAsync(SchemaNode node, List<DatabaseObjectIdentity> result)
    {
        if (node.Type is SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView)
        {
            result.Add(node.Identity!);
            return;
        }
        if (node.Type is SchemaNodeType.TableFolder or SchemaNodeType.ViewFolder or SchemaNodeType.MaterializedViewFolder && !node.IsLoaded)
        {
            var kind = node.Type == SchemaNodeType.TableFolder ? SchemaNodeType.Table :
                node.Type == SchemaNodeType.ViewFolder ? SchemaNodeType.View : SchemaNodeType.MaterializedView;
            var children = await GetChildrenAsync(node.Identity!.Parent!, kind);
            node.Children.Clear();
            foreach (var child in children) node.Children.Add(child);
            node.IsLoaded = true;
        }
        foreach (var child in node.Children) await CollectRelationsAsync(child, result);
    }

    public Task<List<ColumnDefinition>> GetColumnsAsync(DatabaseObjectIdentity relation)
    {
        Validate(relation);
        return ((IStructuredSchemaProvider)session.Provider).GetColumnsAsync(session.ConnectionString, relation);
    }

    public async Task<List<SchemaNode>> GetChildrenAsync(DatabaseObjectIdentity parent, SchemaNodeType kind)
    {
        Validate(parent);
        var snapshot = await session.Catalog.LoadAsync(new(parent, kind));
        if (!snapshot.HasSnapshot) throw new InvalidOperationException(snapshot.Error ?? "Metadata is unavailable.");
        var children = snapshot.Records.Select(ProviderMetadataCatalogSource.Project).ToList();
        foreach (var child in children) Bind(child, parent);
        return children;
    }

    public void Validate(DatabaseObjectIdentity identity)
    {
        if ((identity.ConnectionId != Guid.Empty && identity.ConnectionId != session.Connection.Id) ||
            (identity.Generation != Guid.Empty && identity.Generation != Generation))
            throw new InvalidOperationException("Select the object again from the current connection.");
        if (identity.Catalog != null && _roots.Count > 0 &&
            !_roots.Any(root => NameEquals(root.Identity?.Catalog, identity.Catalog)))
            throw new InvalidOperationException("The saved catalog is unavailable. Select the object again in the explorer.");
    }
}
