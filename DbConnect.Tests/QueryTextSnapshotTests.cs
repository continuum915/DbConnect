using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class QueryTextSnapshotTests
{
    [Fact]
    public void StringBackedSnapshotIsAlreadyMaterialized()
    {
        var text = new string('x', 1024);
        var snapshot = QueryTextSnapshot.FromString(text);
        Assert.True(snapshot.TryGetMaterializedText(out var cached));
        Assert.Same(text, cached);
        Assert.Same(text, snapshot.Text);
    }
    [Fact]
    public void ProbingCachedTextDoesNotMaterializeAnUnreadRevision()
    {
        var reads = 0;
        var snapshot = new QueryTextSnapshot(3, () => { reads++; return new string('x', 3); }, () => new StringReader("xxx"));
        Assert.False(snapshot.TryGetMaterializedText(out var missing));
        Assert.Null(missing);
        Assert.Equal(0, reads);
        var text = snapshot.Text;
        Assert.True(snapshot.TryGetMaterializedText(out var cached));
        Assert.Same(text, cached);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void PublishingNotifiesAndMarksDirtyWithoutMaterializingText()
    {
        var vm = new QueryTabViewModel();
        vm.ClearDirty();
        var reads = 0;
        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.QueryText)) changes++; };
        vm.PublishEditorQueryText(new QueryTextSnapshot(8, () => { reads++; return "SELECT 1"; }, () => new StringReader("SELECT 1")), true);
        Assert.Equal(0, reads);
        Assert.True(vm.IsDirty);
        Assert.Equal(1, changes);
        Assert.Equal("SELECT 1", vm.QueryText);
        Assert.Equal("SELECT 1", vm.QueryText);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ConcurrentConsumersShareOneMaterialization()
    {
        var reads = 0;
        var snapshot = new QueryTextSnapshot(8, () => { Interlocked.Increment(ref reads); return new string("SELECT 1".ToCharArray()); }, () => new StringReader("SELECT 1"));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => snapshot.Text)));
        Assert.All(results, value => Assert.Same(results[0], value));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void EquivalentSnapshotsDoNotDirtyOrNotifyAndCompareWithoutStrings()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT 1" };
        vm.ClearDirty();
        var changes = 0;
        var reads = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.QueryText)) changes++; };
        vm.PublishEditorQueryText(new QueryTextSnapshot(8, () => { reads++; return "SELECT 1"; }, () => new StringReader("SELECT 1")));
        Assert.False(vm.IsDirty);
        Assert.Equal(0, changes);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ExplicitAssignmentsPreserveOldAndNewNotificationValuesAndNoopSemantics()
    {
        var vm = new QueryTabViewModel { QueryText = "SELECT 1" };
        vm.ClearDirty();
        var values = new List<string>();
        vm.PropertyChanging += (_, e) => { if (e.PropertyName == nameof(vm.QueryText)) values.Add("old:" + vm.QueryText); };
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.QueryText)) values.Add("new:" + vm.QueryText); };
        vm.QueryText = "SELECT 1";
        Assert.False(vm.IsDirty);
        Assert.Empty(values);
        vm.QueryText = "SELECT 2";
        Assert.Equal(new[] { "old:SELECT 1", "new:SELECT 2" }, values);
        Assert.True(vm.IsDirty);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("", "", true)]
    [InlineData("abc", "ab", false)]
    public void SnapshotEqualityStreamsWithoutCallingStringFactories(string left, string right, bool equal)
    {
        QueryTextSnapshot Snapshot(string text) => new(text.Length, () => throw new Exception("Unexpected materialization"), () => new StringReader(text));
        Assert.Equal(equal, Snapshot(left).ContentEquals(Snapshot(right)));
    }
}
