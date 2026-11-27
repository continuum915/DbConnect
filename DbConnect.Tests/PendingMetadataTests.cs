using System.Collections.Immutable;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class PendingMetadataTests
{
    [Fact]
    public async Task PendingTransactionSupersedesNonCooperativeMetadataRead()
    {
        var source = new GatedSource();
        using var catalog = new MetadataCatalog(source);
        var old = catalog.LoadAsync(MetadataScope.Root);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var owner = new object();
        catalog.MarkPending(MetadataScope.Root, owner);
        source.Release.SetResult([new(new(null, null, "transaction_local", SchemaNodeType.Table), "transaction_local")]);
        await old;
        Assert.Equal(MetadataLoadState.Pending, catalog.GetSnapshot(MetadataScope.Root).State);
        Assert.Empty(catalog.GetSnapshot(MetadataScope.Root).Records);
    }

    [Fact]
    public async Task PendingTransactionRetainsCommittedSnapshotEvenDuringExplicitRefreshUntilLastOwnerEnds()
    {
        var source = new Source();
        using var catalog = new MetadataCatalog(source);
        var scope = new MetadataScope(new(null, "sales", "orders", SchemaNodeType.Table), SchemaNodeType.Column);
        await catalog.LoadAsync(scope);
        var first = new object();
        var second = new object();
        catalog.MarkPending(MetadataScope.Root, first);
        catalog.MarkPending(MetadataScope.Root, second);
        source.Name = "uncommitted";

        var pending = await catalog.LoadAsync(scope, refresh: true);
        Assert.Equal(MetadataLoadState.Pending, pending.State);
        Assert.Equal("committed", Assert.Single(pending.Records).Identity.Name);
        Assert.Contains("transaction", pending.Error, StringComparison.OrdinalIgnoreCase);
        catalog.EndPending(first);
        Assert.Equal(MetadataLoadState.Pending, (await catalog.LoadAsync(scope)).State);
        catalog.EndPending(second);
        source.Name = "after_commit";
        Assert.Equal("after_commit", Assert.Single((await catalog.LoadAsync(scope)).Records).Identity.Name);
    }

    private sealed class Source : IMetadataCatalogSource
    {
        public string Name { get; set; } = "committed";
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray.Create(new MetadataRecord(new(null, "sales", Name, scope.Kind), Name)));
    }

    private sealed class GatedSource : IMetadataCatalogSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImmutableArray<MetadataRecord>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Release.Task;
        }
    }
}
