using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Renders known segments independently. Raw names may themselves contain dots.</summary>
public static class ObjectNames
{
    public static string Quote(DatabaseProviderCapabilities capabilities, DatabaseObjectIdentity identity) =>
        string.Join(".", new[] { identity.Catalog, identity.Schema, identity.Name }
            .Where(segment => segment is not null).Select(segment => capabilities.QuoteIdentifier(segment!)));

    /// <summary>schema.name unquoted, or just the name where there is no schema, as dump scripts and messages name objects.</summary>
    public static string SchemaQualified(DatabaseObjectIdentity identity) =>
        string.Join(".", new[] { identity.Schema, identity.Name }.Where(s => !string.IsNullOrEmpty(s)));
}
