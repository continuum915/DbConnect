using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Data Compare refuses to apply to a target that changed since the comparison (apply-comparisons/02).</summary>
public sealed class DataSyncDriftTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL, photo BLOB);
        INSERT INTO users VALUES (1, 'Ann', 1.5, x'01'), (2, 'Bo', 2.25, NULL), (3, 'Cy', 3.0, x'0203');
        """;

    private const string Target = """
        CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL, photo BLOB);
        INSERT INTO users VALUES (1, 'Ann', 1.5, x'01'), (2, 'Bob', 0.1, x'FF'), (4, 'Di', 4.0, NULL);
        """;

    private async Task<DataCompareViewModel> ComparedAsync(string provider, int maxRows = 100_000)
    {
        var fix = (string sql) => provider == "DuckDB" ? sql.Replace(" TEXT", " VARCHAR").Replace(" REAL", " DOUBLE") : sql;
        var source = await SessionAsync(provider, "source", fix(Source));
        var target = await SessionAsync(provider, "target", fix(Target));
        var vm = new DataCompareViewModel(new ObservableCollection<DatabaseSession> { source, target })
        {
            SourceSession = source,
            TargetSession = target,
            SourceTableName = "users",
            TargetTableName = "users",
            MaxRows = maxRows
        };
        await vm.RunCompareCommand.ExecuteAsync(null);
        vm.Apply.Confirm = _ => Task.FromResult(true);
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        vm.Apply.AcknowledgeDataLoss = true;
        return vm;
    }

    [Theory]
    [InlineData("SQLite", "UPDATE users SET name = 'Bobby' WHERE id = 2", "row (2) has a different name")]
    [InlineData("SQLite", "UPDATE users SET photo = x'FE' WHERE id = 2", "row (2) has a different photo")]
    [InlineData("SQLite", "DELETE FROM users WHERE id = 4", "row (4) has been deleted")]
    [InlineData("SQLite", "INSERT INTO users VALUES (3, 'Cy?', 0, NULL)", "row (3) has been inserted")]
    [InlineData("DuckDB", "UPDATE users SET score = 0.2 WHERE id = 2", "row (2) has a different score")]
    public async Task AChangedDeletedOrInsertedRowStopsApplyAndNothingRuns(string provider, string change, string named)
    {
        var vm = await ComparedAsync(provider);
        await ExecuteScriptAsync(Session(provider, "target"), change + ";");
        var before = await QueryAsync(Session(provider, "target"), "SELECT id, name FROM users ORDER BY id");

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(SyncOutcomeKind.NotApplied, vm.Apply.Outcome!.Kind);
        Assert.Contains($"The target changed since the comparison: {named}.", vm.Apply.Status);
        Assert.Contains("compare again", vm.Apply.Status);
        Assert.All(vm.Apply.Outcome.Steps, s => Assert.Equal(SyncStepStatus.NotRun, s.Status));
        Assert.Equal(before, await QueryAsync(Session(provider, "target"), "SELECT id, name FROM users ORDER BY id"));
        Assert.True(vm.Apply.RecompareCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task AnUnchangedTargetPassesTheCheckWithFloatsAndBytes(string provider)
    {
        var vm = await ComparedAsync(provider);

        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status);
        Assert.Empty(vm.DiffRows);
    }

    [Fact]
    public async Task ATruncatedComparisonSaysOnlyTheComparedRowsApply()
    {
        var vm = await ComparedAsync("SQLite", maxRows: 2);

        Assert.True(vm.IsTruncated);
        Assert.Contains("-- The comparison stopped at Max Rows (2); only the rows it compared are applied.", vm.Apply.Script);
    }
}
