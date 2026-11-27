namespace DbConnect.Models;

/// <summary>
/// Compares table identities from different sources. The catalog names a table's database where a design
/// reader may leave it out (PostgreSQL), so the database must match only where both know it; the schema
/// and the name must always match, so a same-named table in another schema is never taken for it.
/// </summary>
public static class TableIdentity
{
    public static bool Same(DatabaseObjectIdentity? a, DatabaseObjectIdentity? b) =>
        a != null && b != null && a.Name == b.Name && a.Schema == b.Schema && (a.Catalog == null || b.Catalog == null || a.Catalog == b.Catalog);
}

/// <summary>A table a foreign key can reference, shown by its qualified name.</summary>
public sealed record ReferenceTarget(DatabaseObjectIdentity Identity)
{
    public override string ToString() => TableDesignText.Qualified(Identity);
}
