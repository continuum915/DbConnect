using System.Collections.Immutable;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class MetadataCatalogTests
{
    [Fact]
    public async Task InvalidationIncludesDescendantsButRetainsOtherSchemasAndObjectKinds()
    {
        var database = new DatabaseObjectIdentity(null, null, "db", SchemaNodeType.Database);
        var sales = new DatabaseObjectIdentity(null, "sales", "sales", SchemaNodeType.Schema) { Parent = database };
        var other = sales with { Name = "other", Schema = "other" };
        var table = new DatabaseObjectIdentity(null, "sales", "orders", SchemaNodeType.Table) { Parent = sales };
        var view = table with { Kind = SchemaNodeType.View };
        MetadataScope[] scopes = [new(sales, SchemaNodeType.Table), new(table, SchemaNodeType.Column),
            new(table, SchemaNodeType.Index), new(sales, SchemaNodeType.View), new(view, SchemaNodeType.Column),
            new(other, SchemaNodeType.Table)];
        using var catalog = new MetadataCatalog(new DelegateSource((_, _) => Task.FromResult(ImmutableArray<MetadataRecord>.Empty)));
        foreach (var scope in scopes) await catalog.LoadAsync(scope);

        catalog.Invalidate(scopes[0]);

        Assert.All(scopes.Take(3), scope => Assert.Equal(MetadataLoadState.Stale, catalog.GetSnapshot(scope).State));
        Assert.All(scopes.Skip(3), scope => Assert.Equal(MetadataLoadState.LoadedEmpty, catalog.GetSnapshot(scope).State));
        catalog.Invalidate(new(sales, SchemaNodeType.Schema));
        Assert.Equal(MetadataLoadState.Stale, catalog.GetSnapshot(scopes[4]).State);
        Assert.Equal(MetadataLoadState.LoadedEmpty, catalog.GetSnapshot(scopes[5]).State);
    }

    [Fact]
    public async Task IdenticalLoadsShareAnAtomicSnapshotWhileIndependentScopesProceed()
    {
        var source = new GatedSource();
        using var catalog = new MetadataCatalog(source);
        var scope = new MetadataScope(new(null, "sales", "sales", SchemaNodeType.Schema), SchemaNodeType.Table);
        Assert.Equal(MetadataLoadState.NotLoaded, catalog.GetSnapshot(scope).State);
        var first = catalog.LoadAsync(scope);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = catalog.LoadAsync(scope);
        Assert.Same(first, second);
        Assert.Equal(MetadataLoadState.Loading, catalog.GetSnapshot(scope).State);
        Assert.Empty(catalog.GetSnapshot(scope).Records);
        var independent = await catalog.LoadAsync(new(scope.Parent, SchemaNodeType.View));
        Assert.Equal(MetadataLoadState.LoadedEmpty, independent.State);
        source.Release.SetResult([new MetadataRecord(new(null, "sales", "orders", SchemaNodeType.Table), "orders")]);
        Assert.Equal("orders", Assert.Single((await first).Records).Identity.Name);
        Assert.Same(await first, await second);
    }

    [Fact]
    public async Task FailedRefreshRetainsLastSnapshotRedactsErrorsAndExplicitRetryCanLoadEmpty()
    {
        var reads = 0;
        using var catalog = new MetadataCatalog(new DelegateSource((_, _) =>
        {
            reads++;
            if (reads == 2) throw new IOException("password=secret; host=private");
            return Task.FromResult(reads == 1
                ? ImmutableArray.Create(new MetadataRecord(new(null, null, "orders", SchemaNodeType.Table), "orders"))
                : ImmutableArray<MetadataRecord>.Empty);
        }));
        Assert.Equal(MetadataLoadState.Loaded, (await catalog.LoadAsync(MetadataScope.Root)).State);
        var stale = await catalog.LoadAsync(MetadataScope.Root, refresh: true);
        Assert.Equal(MetadataLoadState.Stale, stale.State);
        Assert.Single(stale.Records);
        Assert.DoesNotContain("secret", stale.Error);
        Assert.Equal(MetadataLoadState.LoadedEmpty, (await catalog.LoadAsync(MetadataScope.Root, refresh: true)).State);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task InvalidationAndNewerRefreshSupersedeLateNonCooperativeReads()
    {
        var first = new GatedSource();
        var reads = 0;
        using var catalog = new MetadataCatalog(new DelegateSource((scope, token) =>
            Interlocked.Increment(ref reads) == 1 ? first.ReadAsync(scope, token) :
                Task.FromResult(ImmutableArray.Create(new MetadataRecord(new(null, null, "new", SchemaNodeType.Table), "new")))));
        var old = catalog.LoadAsync(MetadataScope.Root);
        await first.Entered.Task;
        catalog.Invalidate(MetadataScope.Root);
        var current = await catalog.LoadAsync(MetadataScope.Root);
        first.Release.SetResult([new(new(null, null, "old", SchemaNodeType.Table), "old")]);
        await old;
        Assert.Equal("new", Assert.Single(catalog.GetSnapshot(MetadataScope.Root).Records).Identity.Name);
        Assert.Equal(current.Revision, catalog.GetSnapshot(MetadataScope.Root).Revision);
    }

    [Fact]
    public async Task DisconnectCancelsOwnershipAndLateResultsCannotPopulateAnotherCatalog()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ImmutableArray<MetadataRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var catalog = new MetadataCatalog(new DelegateSource((_, token) => { entered.SetResult(token); return release.Task; }));
        var pending = catalog.LoadAsync(MetadataScope.Root);
        var token = await entered.Task;
        catalog.Dispose();
        Assert.True(token.IsCancellationRequested);
        release.SetResult([new(new(null, null, "late", SchemaNodeType.Table), "late")]);
        await pending;
        Assert.Empty(catalog.GetSnapshot(MetadataScope.Root).Records);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await catalog.LoadAsync(MetadataScope.Root));
        using var other = new MetadataCatalog(new DelegateSource((_, _) => Task.FromResult(ImmutableArray<MetadataRecord>.Empty)));
        Assert.Equal(MetadataLoadState.NotLoaded, other.GetSnapshot(MetadataScope.Root).State);
        Assert.Equal(MetadataLoadState.LoadedEmpty, (await other.LoadAsync(MetadataScope.Root)).State);
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelSharedLoadAndFirstFailureIsNotEmpty()
    {
        var source = new GatedSource();
        using var catalog = new MetadataCatalog(source);
        using var stop = new CancellationTokenSource();
        var waiter = catalog.LoadAsync(MetadataScope.Root, cancellationToken: stop.Token);
        await source.Entered.Task;
        var shared = catalog.LoadAsync(MetadataScope.Root);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        Assert.Equal(MetadataLoadState.Loading, catalog.GetSnapshot(MetadataScope.Root).State);
        source.Release.SetException(new IOException("Password=secret"));
        var failed = await shared;
        Assert.Equal(MetadataLoadState.Failed, failed.State);
        Assert.False(failed.HasSnapshot);
        Assert.NotNull(failed.Error);
        Assert.DoesNotContain("secret", failed.Error);
    }
    private sealed class DelegateSource(Func<MetadataScope, CancellationToken, Task<ImmutableArray<MetadataRecord>>> read) : IMetadataCatalogSource
    {
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken) => read(scope, cancellationToken);
    }
    private sealed class GatedSource : IMetadataCatalogSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImmutableArray<MetadataRecord>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken token)
        {
            if (scope.Kind == SchemaNodeType.View) return Task.FromResult(ImmutableArray<MetadataRecord>.Empty);
            Entered.TrySetResult();
            return Release.Task;
        }
    }
}
