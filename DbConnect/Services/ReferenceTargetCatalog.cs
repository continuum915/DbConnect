using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// The tables a foreign key can reference, read from the session's metadata catalog through the same
/// scopes the explorer uses. Only the connection's own database is listed: other server databases are
/// listed lazily, even once the explorer has opened them, and PostgreSQL and SQL Server cannot reference
/// another database. A listing that fails throws, so a failure never looks like an empty list.
/// </summary>
public static class ReferenceTargetCatalog
{
    public static async Task<IReadOnlyList<DatabaseObjectIdentity>> ListTablesAsync(DatabaseSession session, CancellationToken cancellationToken)
    {
        var tables = new List<DatabaseObjectIdentity>();
        async Task<IEnumerable<MetadataRecord>> Load(MetadataScope scope)
        {
            var snapshot = await session.Catalog.LoadAsync(scope, cancellationToken: cancellationToken);
            if (snapshot.State == MetadataLoadState.Failed)
                throw new TableDefinitionUnavailableException(snapshot.Error ?? "The list of tables could not be read.");
            return snapshot.Records;
        }
        async Task Visit(MetadataRecord record)
        {
            var identity = record.Identity;
            switch (identity.Kind)
            {
                case SchemaNodeType.Table:
                    tables.Add(new DatabaseObjectIdentity(identity.Catalog, identity.Schema, identity.Name, SchemaNodeType.Table));
                    break;
                case SchemaNodeType.TableFolder:
                    foreach (var child in await Load(new(identity.Parent!, SchemaNodeType.Table))) await Visit(child);
                    break;
                case SchemaNodeType.Database when record.IsLazyLoadable:
                    break;
                case SchemaNodeType.Schema or SchemaNodeType.Database:
                    var children = record.Children.IsEmpty ? await Load(new(identity, identity.Kind)) : record.Children;
                    foreach (var child in children) await Visit(child);
                    break;
            }
        }
        foreach (var record in await Load(MetadataScope.Root)) await Visit(record);
        return tables;
    }
}
