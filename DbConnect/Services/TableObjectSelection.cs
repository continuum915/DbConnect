using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Retains a tab's selected address; legacy sessions resolve through catalog discovery once.</summary>
public sealed class TableObjectSelection
{
    public DatabaseObjectIdentity? Identity { get; private set; }
    public string? LegacyName { get; private set; }
    public bool RequiresReselection { get; private set; }
    public long MetadataRevision { get; private set; }
    private bool _hasInitialMetadata;

    public async Task<(List<string> Keys, List<string> Order)> LoadOrderColumnsAsync(
        DatabaseSession session, List<string> keys, List<string> order)
    {
        if (Identity is null || session.Provider is not IStructuredSchemaProvider) return (keys, order);
        var scope = new MetadataScope(Identity, SchemaNodeType.Column);
        var initial = session.Catalog.GetSnapshot(scope);
        // Supplied metadata may describe a table local to the tab's database session.
        if (_hasInitialMetadata && initial.State == MetadataLoadState.NotLoaded && initial.Revision == 0) return (keys, order);
        var snapshot = await session.Catalog.LoadAsync(scope);
        MetadataRevision = snapshot.Revision;
        if (!snapshot.HasSnapshot) return (keys, order);
        return TableMetadataOrder.FromSnapshot(snapshot);
    }

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumsAsync(DatabaseSession session, string compatibilityName) =>
        Identity is null || session.Provider is not IStructuredSchemaProvider ? session.Provider.GetEnumColumnsAsync(session.ConnectionString, compatibilityName)
            : ((IStructuredSchemaProvider)session.Provider).GetEnumColumnsAsync(session.ConnectionString, Identity);

    public string CompatibilityName(SchemaNode? node, string fallback)
    {
        var name = Identity?.Name ?? node?.RawName ?? fallback;
        var schema = Identity?.Schema ?? node?.SchemaName ?? node?.SchemaPath;
        return string.IsNullOrEmpty(schema) ? name : schema + "." + name;
    }

    public TableObjectSelection(DatabaseSession? session, SchemaNode? node)
    {
        if (session != null && node != null)
        {
            Identity = node.Identity ?? session.Objects.Bind(node, null);
            _hasInitialMetadata = node.Children.Any(child => child.Type == SchemaNodeType.Column);
        }
    }

    public void Restore(DatabaseSession session, DatabaseObjectIdentity? identity, string legacyName)
    {
        _hasInitialMetadata = false;
        RequiresReselection = false;
        Identity = null;
        LegacyName = null;
        if (identity != null && identity.ConnectionId != System.Guid.Empty && identity.ConnectionId != session.Connection.Id)
        {
            RequiresReselection = true;
            return;
        }
        Identity = identity is null ? null : session.Objects.Rebind(identity);
        LegacyName = identity is null ? legacyName : null;
    }

    public async Task<bool> ResolveAsync(DatabaseSession session)
    {
        if (LegacyName != null)
        {
            Identity = await session.Objects.ResolveLegacyRelationAsync(LegacyName);
            LegacyName = null;
            RequiresReselection = Identity is null;
        }
        if (Identity != null)
        {
            RequiresReselection = session.Catalog.IsKnownMissing(Identity);
            try { session.Objects.Validate(Identity); }
            catch (System.InvalidOperationException) { RequiresReselection = true; }
        }
        return !RequiresReselection;
    }
}
