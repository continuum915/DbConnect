using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Owns atomic snapshots, per-scope sharing and revision ordering for one connection generation.</summary>
public sealed partial class MetadataCatalog(IMetadataCatalogSource source) : IDisposable
{
    private sealed class Entry
    {
        public MetadataSnapshot Snapshot = new(MetadataLoadState.NotLoaded, 0, []);
        public Task<MetadataSnapshot>? Pending;
        public CancellationTokenSource? Stop;
    }

    private readonly object _sync = new();
    private readonly Dictionary<MetadataScope, Entry> _entries = new();
    private readonly Dictionary<object, HashSet<MetadataScope>> _pendingTransactions = new(ReferenceEqualityComparer.Instance);
    private const string PendingMessage = "Metadata changes are pending in an open transaction. Committed metadata is retained until commit or rollback.";
    private bool _disposed;
    public bool IsDisposed { get { lock (_sync) return _disposed; } }

    public MetadataSnapshot GetSnapshot(MetadataScope scope)
    {
        lock (_sync) return GetEntry(scope).Snapshot;
    }

    public Task<MetadataSnapshot> LoadAsync(MetadataScope scope, bool refresh = false, CancellationToken cancellationToken = default,
        bool invalidateDescendants = true)
    {
        if (refresh && invalidateDescendants) Invalidate(scope);
        Task<MetadataSnapshot> task;
        CancellationTokenSource? previous = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = GetEntry(scope);
            var failedAncestor = !refresh ? _entries.FirstOrDefault(pair => pair.Key != scope &&
                MetadataScopeCoverage.Contains(pair.Key, scope) && pair.Value.Snapshot.Error != null &&
                pair.Value.Snapshot.State is MetadataLoadState.Stale or MetadataLoadState.Failed).Value : null;
            if (IsPending(scope))
            {
                task = Task.FromResult(entry.Snapshot);
            }
            else if (failedAncestor != null)
            {
                previous = entry.Stop;
                entry.Stop = null;
                entry.Pending = null;
                var state = entry.Snapshot.HasSnapshot ? MetadataLoadState.Stale : MetadataLoadState.Failed;
                if (previous != null || entry.Snapshot.State != state || entry.Snapshot.Error != failedAncestor.Snapshot.Error)
                {
                    entry.Snapshot = entry.Snapshot with
                    {
                        Revision = entry.Snapshot.Revision + 1,
                        State = state,
                        Error = failedAncestor.Snapshot.Error
                    };
                }
                task = Task.FromResult(entry.Snapshot);
            }
            else if (!refresh && entry.Pending != null) task = entry.Pending;
            else if (!refresh && entry.Snapshot is { State: MetadataLoadState.Stale, Error: not null })
                task = Task.FromResult(entry.Snapshot);
            else if (!refresh && scope.Kind == SchemaNodeType.Schema && entry.Snapshot.State == MetadataLoadState.Failed)
                task = Task.FromResult(entry.Snapshot);
            else if (!refresh && entry.Snapshot.State is MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)
                task = Task.FromResult(entry.Snapshot);
            else
            {
                previous = entry.Stop;
                var stop = new CancellationTokenSource();
                entry.Stop = stop;
                var revision = entry.Snapshot.Revision + 1;
                entry.Snapshot = entry.Snapshot with { State = MetadataLoadState.Loading, Revision = revision, Error = null };
                var completion = new TaskCompletionSource<MetadataSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Pending = task = completion.Task;
                _ = Task.Run(() => ReadAsync(scope, entry, revision, stop, completion));
            }
        }
        if (previous != null) Cancel(previous);
        // A caller abandoning a shared request does not cancel other consumers' load.
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task ReadAsync(MetadataScope scope, Entry entry, long revision, CancellationTokenSource stop,
        TaskCompletionSource<MetadataSnapshot> completion)
    {
        MetadataSnapshot? result = null;
        try
        {
            var records = await source.ReadAsync(scope, stop.Token).WaitAsync(stop.Token);
            result = new(records.IsEmpty ? MetadataLoadState.LoadedEmpty : MetadataLoadState.Loaded, revision, records, true);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Driver messages can contain endpoints, credentials or SQL. UI diagnostics are deliberately bounded.
            result = new(MetadataLoadState.Failed, revision, [], Error: "Metadata could not be loaded. Retry to try again.");
        }
        lock (_sync)
        {
            if (!_disposed && entry.Snapshot.Revision == revision)
            {
                entry.Pending = null;
                entry.Stop = null;
                entry.Snapshot = result is null
                    ? entry.Snapshot with { State = entry.Snapshot.HasSnapshot ? MetadataLoadState.Stale : MetadataLoadState.NotLoaded }
                    : result.State == MetadataLoadState.Failed
                        ? entry.Snapshot with { State = entry.Snapshot.HasSnapshot ? MetadataLoadState.Stale : MetadataLoadState.Failed, Error = result.Error }
                        : result;
            }
            completion.TrySetResult(entry.Snapshot);
        }
        lock (stop) stop.Dispose();
    }

    public void Invalidate(MetadataScope scope)
    {
        List<CancellationTokenSource> stops = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            GetEntry(scope);
            var entries = _entries.Where(pair => MetadataScopeCoverage.Contains(scope, pair.Key)).ToList();
            foreach (var (key, entry) in entries)
            {
                if (entry.Stop != null) stops.Add(entry.Stop);
                entry.Stop = null;
                entry.Pending = null;
                entry.Snapshot = entry.Snapshot with
                {
                    Revision = entry.Snapshot.Revision + 1,
                    State = IsPending(key) ? MetadataLoadState.Pending : entry.Snapshot.HasSnapshot ? MetadataLoadState.Stale : MetadataLoadState.NotLoaded,
                    Error = IsPending(key) ? PendingMessage : null
                };
            }
        }
        foreach (var stop in stops) Cancel(stop);
    }

    public void MarkPending(MetadataScope scope, object transactionOwner)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pendingTransactions.TryGetValue(transactionOwner, out var scopes))
                _pendingTransactions.Add(transactionOwner, scopes = []);
            scopes.Add(scope);
        }
        Invalidate(scope);
    }

    public bool EndPending(object transactionOwner)
    {
        HashSet<MetadataScope>? scopes;
        lock (_sync)
        {
            if (_disposed || !_pendingTransactions.Remove(transactionOwner, out scopes)) return false;
        }
        foreach (var scope in scopes) Invalidate(scope);
        return true;
    }

    private bool IsPending(MetadataScope scope) => _pendingTransactions.Values
        .Any(scopes => scopes.Any(pending => MetadataScopeCoverage.Contains(pending, scope)));

    public void Dispose()
    {
        List<CancellationTokenSource> stops = [];
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingTransactions.Clear();
            foreach (var entry in _entries.Values)
            {
                if (entry.Stop != null) stops.Add(entry.Stop);
                entry.Stop = null;
                entry.Pending = null;
                entry.Snapshot = new(MetadataLoadState.NotLoaded, entry.Snapshot.Revision + 1, []);
            }
        }
        foreach (var stop in stops) Cancel(stop);
    }

    private static void Cancel(CancellationTokenSource stop)
    {
        lock (stop)
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* The load already released its registrations. */ }
        }
    }

    private Entry GetEntry(MetadataScope scope)
    {
        if (!_entries.TryGetValue(scope, out var entry))
        {
            entry = new();
            if (IsPending(scope)) entry.Snapshot = entry.Snapshot with { State = MetadataLoadState.Pending, Error = PendingMessage };
            _entries.Add(scope, entry);
        }
        return entry;
    }
}
