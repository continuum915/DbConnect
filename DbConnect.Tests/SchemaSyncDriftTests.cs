using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Schema Diff refuses to apply to a target that changed since the comparison (apply-comparisons/05).</summary>
public sealed class SchemaSyncDriftTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price NUMERIC);
        CREATE TABLE fresh (id INTEGER PRIMARY KEY);
        """;

    private const string Target = """
        CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE stale (id INTEGER PRIMARY KEY);
        CREATE VIEW stale_view AS SELECT id FROM stale;
        """;

    private async Task<SchemaDiffViewModel> PreviewedAsync(string provider = "SQLite")
    {
        var fix = (string sql) => provider == "DuckDB" ? sql.Replace(" TEXT", " VARCHAR") : sql;
        var source = await SessionAsync(provider, "source", fix(Source));
        var target = await SessionAsync(provider, "target", fix(Target));
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source, TargetSession = target };
        await vm.RunCompareCommand.ExecuteAsync(null);
        vm.Apply.Confirm = _ => Task.FromResult(true);
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.Apply.Plan != null, vm.Apply.Status);
        vm.Apply.AcknowledgeDataLoss = true;
        return vm;
    }

    private Task<string> TargetObjectsAsync() => QueryAsync(Session("SQLite", "target"),
        "SELECT type || ' ' || name FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY name");

    [Theory]
    [InlineData("ALTER TABLE items ADD COLUMN extra TEXT", "Table main.items is not as it was")]
    [InlineData("CREATE TABLE fresh (other INTEGER)", "Table main.fresh now exists")]
    [InlineData("DROP VIEW stale_view", "View main.stale_view is not as it was")]
    public async Task AChangeBetweenPreviewAndApplyIsRefusedAndNothingRuns(string change, string named)
    {
        var vm = await PreviewedAsync();
        await ExecuteScriptAsync(Session("SQLite", "target"), change + ";");
        var before = await TargetObjectsAsync();

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(SyncOutcomeKind.NotApplied, vm.Apply.Outcome!.Kind);
        Assert.Contains($"The target changed since the comparison: {named}.", vm.Apply.Status);
        Assert.All(vm.Apply.Outcome.Steps, s => Assert.Equal(SyncStepStatus.NotRun, s.Status));
        Assert.Equal(before, await TargetObjectsAsync());
        Assert.True(vm.Apply.RecompareCommand.CanExecute(null));
    }

    [Fact]
    public async Task AChangeBetweenCompareAndPreviewIsRefusedAtPreview()
    {
        var source = await SessionAsync("SQLite", "source", Source);
        var target = await SessionAsync("SQLite", "target", Target);
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source, TargetSession = target };
        await vm.RunCompareCommand.ExecuteAsync(null);
        await ExecuteScriptAsync(target, "DROP VIEW stale_view;");

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        Assert.Null(vm.Apply.Plan);
        Assert.True(vm.Apply.Status.StartsWith("The target changed since the comparison: view stale_view is no longer on the target.", StringComparison.OrdinalIgnoreCase), vm.Apply.Status);
        await vm.Apply.RecompareCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.DiffItems, i => i.ObjectName == "stale_view");
    }

    [Fact]
    public async Task AnUnchangedTargetPassesTheCheck()
    {
        var vm = await PreviewedAsync("DuckDB");

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log);
    }
}
