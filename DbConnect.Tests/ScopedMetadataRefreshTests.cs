using System.Collections.Immutable;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class ScopedMetadataRefreshTests
{
    [Fact]
    public async Task FailedDatabaseRefreshRetainsDescendantsUntilExplicitRetry()
    {
        var source = new RecoveringSource();
        using var catalog = new MetadataCatalog(source);
        var table = new DatabaseObjectIdentity("db", "sales", "orders", SchemaNodeType.Table);
        var columns = new MetadataScope(table, SchemaNodeType.Column);
        await catalog.LoadAsync(MetadataScope.Root);
        await catalog.LoadAsync(columns);
        source.FailRoot = true;
        await catalog.LoadAsync(MetadataScope.Root, refresh: true);
        source.FailRoot = false;
        source.Name = "recovered";

        var retained = await catalog.LoadAsync(columns);
        Assert.Equal(MetadataLoadState.Stale, retained.State);
        Assert.Equal("original", Assert.Single(retained.Records).Identity.Name);
        Assert.Same(retained, await catalog.LoadAsync(columns));
        await catalog.LoadAsync(MetadataScope.Root, refresh: true);
        Assert.Equal("recovered", Assert.Single((await catalog.LoadAsync(columns)).Records).Identity.Name);
    }

    [Fact]
    public async Task RefreshingFolderInvalidatesOnlyItsObjectDetails()
    {
        var schema = new DatabaseObjectIdentity("db", "sales", "sales", SchemaNodeType.Schema);
        var table = new DatabaseObjectIdentity("db", "sales", "orders", SchemaNodeType.Table) { Parent = schema };
        var view = table with { Kind = SchemaNodeType.View };
        var columns = new MetadataScope(table, SchemaNodeType.Column);
        var viewColumns = new MetadataScope(view, SchemaNodeType.Column);
        using var catalog = new MetadataCatalog(new Source());
        await catalog.LoadAsync(columns);
        await catalog.LoadAsync(viewColumns);
        await catalog.LoadAsync(new(schema, SchemaNodeType.Table), refresh: true);
        Assert.Equal(MetadataLoadState.Stale, catalog.GetSnapshot(columns).State);
        Assert.Equal(MetadataLoadState.Loaded, catalog.GetSnapshot(viewColumns).State);
    }

    [Fact]
    public async Task SchemaInvalidationRetainsSnapshotsAndDoesNotInvalidateOtherSchemasOrConnections()
    {
        var sales = new DatabaseObjectIdentity("db", "sales", "sales", SchemaNodeType.Schema);
        var archive = sales with { Schema = "archive", Name = "archive" };
        var orders = new DatabaseObjectIdentity("db", "sales", "orders", SchemaNodeType.Table) { Parent = sales };
        var tables = new MetadataScope(sales, SchemaNodeType.Table);
        var columns = new MetadataScope(orders, SchemaNodeType.Column);
        var other = new MetadataScope(archive, SchemaNodeType.Table);
        using var catalog = new MetadataCatalog(new Source());
        using var independent = new MetadataCatalog(new Source());
        foreach (var scope in new[] { tables, columns, other }) await catalog.LoadAsync(scope);
        await independent.LoadAsync(columns);

        catalog.Invalidate(new(sales, SchemaNodeType.Schema));

        Assert.Equal(MetadataLoadState.Stale, catalog.GetSnapshot(tables).State);
        Assert.Equal(MetadataLoadState.Stale, catalog.GetSnapshot(columns).State);
        Assert.Single(catalog.GetSnapshot(columns).Records);
        Assert.Equal(MetadataLoadState.Loaded, catalog.GetSnapshot(other).State);
        Assert.Equal(MetadataLoadState.Loaded, independent.GetSnapshot(columns).State);
    }

    private sealed class Source : IMetadataCatalogSource
    {
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray.Create(new MetadataRecord(
                new(null, null, "retained", scope.Kind), "retained")));
    }

    private sealed class RecoveringSource : IMetadataCatalogSource
    {
        public bool FailRoot { get; set; }
        public string Name { get; set; } = "original";
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken)
        {
            if (scope == MetadataScope.Root && FailRoot) throw new IOException("offline");
            return Task.FromResult(ImmutableArray.Create(new MetadataRecord(new(null, null, Name, scope.Kind), Name)));
        }
    }
}
