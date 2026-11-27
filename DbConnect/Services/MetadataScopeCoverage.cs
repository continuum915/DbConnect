using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Defines the descendant scopes owned by an explicit metadata refresh.</summary>
internal static class MetadataScopeCoverage
{
    public static bool Contains(MetadataScope scope, MetadataScope candidate)
    {
        if (scope == MetadataScope.Root || scope == candidate) return true;
        for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
        {
            if (scope.Kind is SchemaNodeType.Schema or SchemaNodeType.Column && parent == scope.Parent)
                return true;
            if (parent.Kind == scope.Kind && parent.Parent == scope.Parent) return true;
        }
        return false;
    }
}
