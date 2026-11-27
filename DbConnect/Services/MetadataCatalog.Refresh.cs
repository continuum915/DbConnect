using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public sealed partial class MetadataCatalog
{
    internal IReadOnlyList<MetadataScope> GetRequestedScopes(MetadataScope scope)
    {
        lock (_sync) return _entries.Where(pair => scope.Contains(pair.Key) &&
            (pair.Value.Snapshot.State != MetadataLoadState.NotLoaded || pair.Value.Pending != null)).Select(pair => pair.Key).ToList();
    }

    public event Func<MetadataScope, Task>? Refreshed;

    internal async Task PublishRefreshAsync(MetadataScope scope)
    {
        if (Refreshed is not { } subscribers) return;
        foreach (Func<MetadataScope, Task> subscriber in subscribers.GetInvocationList()) await subscriber(scope);
    }

    public bool IsKnownMissing(DatabaseObjectIdentity identity)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(MetadataScope.ListingFor(identity), out var listing) &&
                listing.Snapshot.State is MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)
                return !listing.Snapshot.Records.Any(record => record.Identity == identity);
            if (!_entries.TryGetValue(MetadataScope.Root, out var root) ||
                root.Snapshot.State is not (MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)) return false;
            var owner = Flatten(root.Snapshot.Records).FirstOrDefault(record => record.Identity == identity.Parent);
            if (owner == null) return identity.Parent != null && IsKnownMissing(identity.Parent);
            if (owner.Children.Any(record => record.Identity.Kind is SchemaNodeType.TableFolder or
                SchemaNodeType.ViewFolder or SchemaNodeType.MaterializedViewFolder)) return false;
            return !owner.Children.Any(record => record.Identity == identity);
        }
    }

    private static IEnumerable<MetadataRecord> Flatten(IEnumerable<MetadataRecord> records) =>
        records.SelectMany(record => new[] { record }.Concat(Flatten(record.Children)));
}
