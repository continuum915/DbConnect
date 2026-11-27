using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>How a dump recognises its tables in foreign-key metadata and snapshot prerequisites, per provider's naming.</summary>
public sealed class DumpTableNamesTests
{
    private static DatabaseObjectIdentity T(string? catalog, string? schema, string name) => new(catalog, schema, name, SchemaNodeType.Table);

    [Theory]
    [InlineData("shop.orders", 1)]             // MySQL: ForeignKeyService names database.table; identities have no schema
    [InlineData("`shop`.`orders`", 1)]         // MySQL snapshot prerequisite
    [InlineData("public.\"Order\"", 2)]        // PostgreSQL quote_ident quotes only the parts that need it
    [InlineData("\"public\".\"Order\"", 2)]
    [InlineData("public.Order", 2)]            // ForeignKeyService output
    [InlineData("[dbo].[Line Items]", 3)]      // SQL Server
    [InlineData("s.x.Parent.T", 4)]            // DuckDB: dotted names, unquoted
    [InlineData("\"s.x\".\"Parent.T\"", 4)]
    [InlineData("CUSTOMERS", 0)]               // SQLite REFERENCES keeps the case it was written in
    [InlineData("\"customers\"", 0)]
    public void FindsTheTableHoweverTheProviderSpellsIt(string spelled, int expected)
    {
        var names = new DumpTableNames([
            T(null, "main", "customers"),
            T("shop", null, "orders"),
            T("db", "public", "Order"),
            T("db", "dbo", "Line Items"),
            T(null, "s.x", "Parent.T")
        ]);

        Assert.Equal(expected, names.Find(spelled));
    }

    [Theory]
    [InlineData("other.orders")]
    [InlineData("Order")]          // two tables share the bare name in different schemas
    [InlineData("missing")]
    public void UnknownOrAmbiguousNamesMatchNothing(string spelled)
    {
        var names = new DumpTableNames([T("db", "public", "Order"), T("db", "audit", "Order"), T("shop", null, "orders")]);

        Assert.Null(names.Find(spelled));
    }

    [Theory]
    [InlineData(null, "main", "CUSTOMERS", 0)]   // SQLite: the reference keeps the case it was written in
    [InlineData("shop", null, "orders", 1)]      // MySQL: the database is the catalog
    [InlineData(null, "public", "Order", 2)]     // PostgreSQL and SQL Server leave the catalog out
    [InlineData(null, "s.x", "Parent.T", 4)]     // DuckDB: dots inside names are not separators
    [InlineData(null, "audit", "Order", null)]
    [InlineData("other", null, "orders", null)]
    public void FindsAReferencedTableByItsRawNames(string? catalog, string? schema, string name, int? expected)
    {
        var names = new DumpTableNames([
            T(null, "main", "customers"),
            T("shop", null, "orders"),
            T("db", "public", "Order"),
            T("db", "dbo", "Line Items"),
            T(null, "s.x", "Parent.T")
        ]);

        Assert.Equal(expected, names.Find(T(catalog, schema, name)));
    }
}
