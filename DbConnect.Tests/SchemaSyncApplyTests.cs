using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Schema Diff Preview → Apply of objects only in one database and changed views (apply-comparisons/03).</summary>
public sealed class SchemaSyncApplyTests : DumpTestDatabases
{
    // new_parent and new_child (which references it) and new_view exist only in the source; shared is in both;
    // shared_view differs; old_table and old_view exist only in the target.
    private const string Source = """
        CREATE TABLE shared (id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE new_parent (id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE);
        CREATE TABLE new_child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES new_parent(id), note TEXT);
        CREATE INDEX new_child_note ON new_child(note);
        CREATE VIEW new_view AS SELECT id, code FROM new_parent;
        CREATE VIEW shared_view AS SELECT id, name FROM shared WHERE id > 1;
        """;

    private const string Target = """
        CREATE TABLE shared (id INTEGER PRIMARY KEY, name TEXT);
        CREATE VIEW shared_view AS SELECT id FROM shared;
        CREATE TABLE old_table (id INTEGER PRIMARY KEY);
        INSERT INTO old_table VALUES (1);
        CREATE VIEW old_view AS SELECT id FROM old_table;
        """;

    private static string For(string provider, string sql) => provider == "DuckDB" ? sql.Replace(" TEXT", " VARCHAR") : sql;

    private async Task<SchemaDiffViewModel> ComparedAsync(string provider, string? targetProvider = null)
    {
        var source = await SessionAsync(provider, "source", For(provider, Source));
        var target = await SessionAsync(targetProvider ?? provider, "target", For(targetProvider ?? provider, Target));
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source, TargetSession = target };
        await vm.RunCompareCommand.ExecuteAsync(null);
        vm.Apply.Confirm = _ => Task.FromResult(true);
        return vm;
    }

    private static IEnumerable<string> Differences(SchemaDiffViewModel vm) => vm.DiffItems
        .Where(i => i.ChangeType != SchemaDiffChangeType.None).Select(i => $"{i.ChangeType} {i.ObjectKind} {i.ObjectName}").OrderBy(s => s);

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task CreatesDropsAndReplacesThenReComparesEqual(string provider)
    {
        var vm = await ComparedAsync(provider);
        Assert.Contains("Add Table new_child", Differences(vm));
        Assert.Contains("Remove Table old_table", Differences(vm));

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        var script = vm.Apply.Script;
        // Dependents are dropped first, referenced tables created first, views after their tables.
        Assert.True(script.IndexOf("DROP VIEW", StringComparison.Ordinal) < script.IndexOf("DROP TABLE", StringComparison.Ordinal), script);
        Assert.True(script.IndexOf("new_parent", StringComparison.Ordinal) < script.IndexOf("CREATE TABLE \"main\".\"new_child\"", StringComparison.OrdinalIgnoreCase)
                    || script.IndexOf("new_parent", StringComparison.Ordinal) < script.IndexOf("new_child", StringComparison.Ordinal), script);
        Assert.True(script.IndexOf("new_view", StringComparison.Ordinal) > script.IndexOf("new_parent", StringComparison.Ordinal), script);
        Assert.Contains("-- DESTRUCTIVE: Drops table main.old_table and every row in it.", script);
        Assert.False(vm.Apply.ApplyCommand.CanExecute(null));
        vm.Apply.AcknowledgeDataLoss = true;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log);
        Assert.Empty(Differences(vm));
        Assert.Equal(await SnapshotAsync(Session(provider, "source"), "new_child"), await SnapshotAsync(Session(provider, "target"), "new_child"));
    }

    [Fact]
    public async Task UntickedDifferencesStayAndAFailureRollsEverythingBack()
    {
        var vm = await ComparedAsync("SQLite");
        foreach (var item in vm.DiffItems.Where(i => i.ObjectName is "old_table" or "old_view")) item.IsSelected = false;
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.False(vm.Apply.HasDestructive);
        // Something already holds the name the plan creates, so its CREATE fails after the earlier steps ran.
        await ExecuteScriptAsync(Session("SQLite", "target"), "CREATE TABLE new_view (x INTEGER);");

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(SyncOutcomeKind.NotApplied, vm.Apply.Outcome!.Kind);
        Assert.Contains("Every step was rolled back.", vm.Apply.Status);
        Assert.Equal("new_view\nold_table\nold_view\nshared\nshared_view", await QueryAsync(Session("SQLite", "target"),
            "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY name"));
    }

    [Fact]
    public async Task BetweenTwoEnginesApplyIsRefusedAndTheScriptStays()
    {
        var vm = await ComparedAsync("SQLite", "DuckDB");

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        Assert.Null(vm.Apply.Plan);
        Assert.Contains("Apply needs two connections of one engine", vm.Apply.Status);
        Assert.False(vm.Apply.ApplyCommand.CanExecute(null));
        await vm.GenerateScriptCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.MigrationScript));
    }
}
