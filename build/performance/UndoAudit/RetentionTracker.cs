using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DbConnect.ViewModels;

namespace UndoAudit;

public sealed class RetentionTracker
{
    private static readonly PropertyInfo? SnapshotProperty = typeof(QueryTabViewModel).GetProperty("CurrentQueryTextSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
    public List<WeakReference> Strings { get; } = [];
    private readonly List<WeakReference> _snapshots = [];
    private readonly List<WeakReference> _sources = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void TrackSnapshot(QueryTabViewModel vm)
    {
        var snapshot = SnapshotProperty?.GetValue(vm);
        if (snapshot == null) return;
        _snapshots.Add(new WeakReference(snapshot));
        var factory = snapshot.GetType().GetField("_openReader", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(snapshot) as Delegate;
        if (factory?.Target is { } source) _sources.Add(new WeakReference(source));
    }

    public static bool ModelEquals(QueryTabViewModel vm, string expected)
    {
        var snapshot = SnapshotProperty?.GetValue(vm);
        if (snapshot == null) return vm.QueryText == expected;
        var type = snapshot.GetType();
        var other = type.GetMethod("FromString", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [expected]);
        return (bool)type.GetMethod("ContentEquals")!.Invoke(snapshot, [other])!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public object Diagnose(QueryTabViewModel vm, string[] expected)
    {
        // Deliberately ensure the current revision has been consumed before checking release.
        var current = vm.QueryText;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var currentSnapshot = SnapshotProperty?.GetValue(vm);
        var strings = Alive(Strings).Where(value => !ReferenceEquals(value, current) && !expected.Any(e => ReferenceEquals(value, e))).Count();
        var snapshots = Alive(_snapshots).Where(value => !ReferenceEquals(value, currentSnapshot)).Count();
        var sources = Alive(_sources).Count;
        var reader = currentSnapshot?.GetType().GetField("_openReader", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(currentSnapshot);
        var process = Process.GetCurrentProcess(); process.Refresh();
        var info = GC.GetGCMemoryInfo();
        return new
        {
            ManagedBytes = GC.GetTotalMemory(false), info.HeapSizeBytes, info.FragmentedBytes, info.TotalCommittedBytes,
            process.PrivateMemorySize64, ObsoleteStringsAlive = strings, ObsoleteSnapshotsAlive = snapshots,
            SourceWrappersAlive = sources, CurrentReaderFactoryAlive = reader != null,
            TrackedStrings = Strings.Count, TrackedSnapshots = _snapshots.Count, TrackedSources = _sources.Count
        };
    }

    private static HashSet<object> Alive(List<WeakReference> references)
    {
        var values = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var reference in references) if (reference.Target is { } value) values.Add(value);
        return values;
    }
}
