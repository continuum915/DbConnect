using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Data Compare Preview → Apply through the shared sync plan and executor (apply-comparisons/01).</summary>
public sealed class DataSyncApplyTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER, photo BLOB);
        INSERT INTO users VALUES (1, 'Ann', 10, x'01'), (2, 'Bo', 20, NULL), (3, 'Cy', 30, NULL), (5, 'Ed', 50, NULL);
        """;

    // Row 2 differs, row 3 is missing, row 4 is only in the target, row 5 needs an insert; row 1 is equal.
    private const string Target = """
        CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER CHECK (score < 45), photo BLOB);
        INSERT INTO users VALUES (1, 'Ann', 10, x'01'), (2, 'Bob', 21, x'FF'), (4, 'Di', 40, NULL);
        """;

    private async Task<DataCompareViewModel> ComparedAsync(string provider = "SQLite", string? target = null)
    {
        var source = await SessionAsync(provider, "source", Source);
        var targetSession = await SessionAsync(provider, "target", target ?? Target);
        var vm = new DataCompareViewModel(new ObservableCollection<DatabaseSession> { source, targetSession })
        {
            SourceSession = source,
            TargetSession = targetSession,
            SourceTableName = "users",
            TargetTableName = "users"
        };
        await vm.RunCompareCommand.ExecuteAsync(null);
        return vm;
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task TickedDifferencesApplyInOneTransactionAndReCompareEqual(string provider)
    {
        // DuckDB's target has no CHECK, so row 5 inserts there too.
        var vm = await ComparedAsync(provider, provider == "DuckDB" ? Target.Replace(" TEXT", " VARCHAR").Replace("CHECK (score < 45)", "") : null);
        if (provider == "DuckDB")
            Assert.True(vm.DiffRows.Count == 4, vm.CompareStatus);
        // Leave row 5 out: the CHECK would refuse it on SQLite.
        vm.DiffRows.Single(r => r.Diff!.PrimaryKeyValue.Contains('5')).IsSelected = false;
        DestructiveActionPreview? asked = null;
        vm.Apply.Confirm = preview => { asked = preview; return Task.FromResult(true); };

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Apply.Plan!.Steps.Count);
        Assert.Contains("UPDATE \"users\" SET \"name\" = 'Bo', \"score\" = 20, \"photo\" = NULL WHERE \"id\" = 2;", vm.Apply.Script);
        Assert.Contains("INSERT INTO \"users\" (\"id\", \"name\", \"score\", \"photo\") VALUES (3, 'Cy', 30, NULL);", vm.Apply.Script);
        Assert.Contains("-- DESTRUCTIVE: Deletes the target row", vm.Apply.Script);
        Assert.True(vm.Apply.HasDestructive);
        Assert.False(vm.Apply.ApplyCommand.CanExecute(null));
        vm.Apply.AcknowledgeDataLoss = true;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Equal("target", asked.ObjectName);
        Assert.Equal("Development", asked.EnvironmentTier);
        Assert.Equal(SyncOutcomeKind.Applied, vm.Apply.Outcome!.Kind);
        Assert.Equal("Applied 3 steps · 3 rows affected. Compared again.", vm.Apply.Status);
        Assert.Equal("1|Ann|10\n2|Bo|20\n3|Cy|30", await QueryAsync(Session(provider, "target"), "SELECT id, name, score FROM users ORDER BY id"));
        // Compared again: only the row left out still differs.
        var left = Assert.Single(vm.DiffRows);
        Assert.Contains("5", left.Diff!.PrimaryKeyValue);
    }

    [Fact]
    public async Task AFailingStepRollsEveryStepBackAndTheLogSaysSo()
    {
        var vm = await ComparedAsync();
        vm.Apply.Confirm = _ => Task.FromResult(true);
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        vm.Apply.AcknowledgeDataLoss = true;

        // Row 5 breaks the target's CHECK after the other steps ran.
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        var outcome = vm.Apply.Outcome!;
        Assert.Equal(SyncOutcomeKind.NotApplied, outcome.Kind);
        Assert.Contains("Every step was rolled back.", outcome.Error);
        Assert.Equal(SyncStepStatus.Failed, outcome.Steps.Single(s => s.Step.Object.Contains('5')).Status);
        Assert.All(outcome.Steps.Where(s => !s.Step.Object.Contains('5')), s => Assert.Equal(SyncStepStatus.RolledBack, s.Status));
        Assert.Equal("1|Ann\n2|Bob\n4|Di", await QueryAsync(Session("SQLite", "target"), "SELECT id, name FROM users ORDER BY id"));
        Assert.Contains("Failed", vm.Apply.Log);

        var sql = Path.Combine(DumpDirectory, "log.sql");
        var txt = Path.Combine(DumpDirectory, "log.txt");
        vm.Apply.PickLogPath = _ => Task.FromResult<string?>(sql);
        await vm.Apply.SaveLogCommand.ExecuteAsync(null);
        vm.Apply.PickLogPath = _ => Task.FromResult<string?>(txt);
        await vm.Apply.SaveLogCommand.ExecuteAsync(null);
        var saved = await File.ReadAllTextAsync(sql);
        Assert.StartsWith("-- Nothing was applied:", saved);
        Assert.Contains("-- Row (5): Failed", saved);
        Assert.Contains("INSERT INTO \"users\"", saved);
        Assert.StartsWith("Nothing was applied:", await File.ReadAllTextAsync(txt));
    }

    [Fact]
    public async Task DecliningTheConfirmationRunsNothing()
    {
        var vm = await ComparedAsync();
        vm.Apply.Confirm = _ => Task.FromResult(false);
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        vm.Apply.AcknowledgeDataLoss = true;

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.Null(vm.Apply.Outcome);
        Assert.Equal("Not applied: the changes were not confirmed.", vm.Apply.Status);
        Assert.Equal("1|Ann\n2|Bob\n4|Di", await QueryAsync(Session("SQLite", "target"), "SELECT id, name FROM users ORDER BY id"));
    }

    [Fact]
    public async Task TickingAnotherDifferenceAsksForANewPreviewAndOtherPoliciesApplyNothing()
    {
        var vm = await ComparedAsync();
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Apply.Plan);

        vm.DiffRows[0].IsSelected = false;
        Assert.Null(vm.Apply.Plan);
        Assert.Equal("", vm.Apply.Script);

        vm.ConflictPolicy = ConflictResolutionPolicy.TargetWins;
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.Null(vm.Apply.Plan);
        Assert.Contains("choose SourceWins", vm.Apply.Status);
    }

    [Fact]
    public async Task WithoutOneTransactionTheFirstFailureStopsTheRestAndTheAppliedStepsStay()
    {
        var target = await SessionAsync("SQLite", "target", "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER CHECK (v < 3));");
        SyncStep Insert(int id) => new($"Row {id}", "Insert",
            new SqlCommandSpec($"INSERT INTO t VALUES ({id}, {id})", new Dictionary<string, object?>()), $"INSERT INTO t VALUES ({id}, {id})");
        var plan = new SyncPlan("target", null, "Development", [Insert(1), Insert(2), Insert(3), Insert(0)], Transactional: false);

        var outcome = await SyncPlanExecutor.ExecuteAsync(target, plan);

        Assert.Equal(SyncOutcomeKind.Partial, outcome.Kind);
        Assert.Equal([SyncStepStatus.Applied, SyncStepStatus.Applied, SyncStepStatus.Failed, SyncStepStatus.NotRun], outcome.Steps.Select(s => s.Status));
        Assert.StartsWith("2 of 4 steps were applied and remain", outcome.Summary);
        Assert.Equal("1\n2", await QueryAsync(target, "SELECT id FROM t ORDER BY id"));
    }

    [Fact]
    public async Task ContinueOnErrorRunsTheStepsAfterAFailureAndListsWhatFailed()
    {
        var target = await SessionAsync("SQLite", "target", "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER CHECK (v < 3));");
        SyncStep Insert(int id) => new($"Row {id}", "Insert",
            new SqlCommandSpec($"INSERT INTO t VALUES ({id}, {id})", new Dictionary<string, object?>()), $"INSERT INTO t VALUES ({id}, {id})")
        {
            CommitsImplicitly = true
        };
        var plan = new SyncPlan("target", null, "Development", [Insert(1), Insert(3), Insert(2), Insert(4)], Transactional: false);
        Assert.Contains("-- 4 steps commit at once and cannot be rolled back (marked below).", plan.Script(sql => sql + ";"));

        var outcome = await SyncPlanExecutor.ExecuteAsync(target, plan, continueOnError: true);

        Assert.Equal(SyncOutcomeKind.Partial, outcome.Kind);
        Assert.Equal([SyncStepStatus.Applied, SyncStepStatus.Failed, SyncStepStatus.Applied, SyncStepStatus.Failed], outcome.Steps.Select(s => s.Status));
        Assert.StartsWith("2 of 4 steps were applied and remain; 2 failed and were skipped: Row 3:", outcome.Summary);
        Assert.Equal("1\n2", await QueryAsync(target, "SELECT id FROM t ORDER BY id"));
    }
}
