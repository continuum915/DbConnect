using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Refreshes a listing and the previously requested metadata that it owns.</summary>
public static class MetadataRefresh
{
    public static async Task<MetadataSnapshot> RefreshAsync(this MetadataCatalog catalog, MetadataScope scope)
    {
        var affected = catalog.GetRequestedScopes(scope);
        catalog.Invalidate(scope);
        var revisions = affected.ToDictionary(item => item, item => catalog.GetSnapshot(item).Revision);
        // Re-read membership without invalidating siblings' detail scopes. A renamed object is a
        // removed address plus a new address; never redirect the existing selection to its new name.
        if (scope.Kind is SchemaNodeType.Column or SchemaNodeType.Schema && scope.Parent is { } relation)
            await catalog.LoadAsync(MetadataScope.ListingFor(relation), refresh: true, invalidateDescendants: false);
        var result = await catalog.LoadAsync(scope);
        if (result.State is not (MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty))
        {
            await catalog.PublishRefreshAsync(scope);
            return result;
        }
        foreach (var child in affected.Where(item => item != scope).OrderBy(Depth))
        {
            if (catalog.IsDisposed || catalog.GetSnapshot(scope).Revision != result.Revision) break;
            if (catalog.GetSnapshot(child).Revision == revisions[child]) await catalog.LoadAsync(child, refresh: true, invalidateDescendants: false);
        }
        await catalog.PublishRefreshAsync(scope);
        return catalog.GetSnapshot(scope);
    }

    private static int Depth(MetadataScope scope)
    {
        var depth = 0;
        for (var parent = scope.Parent; parent != null; parent = parent.Parent) depth++;
        return depth;
    }
}
