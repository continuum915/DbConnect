using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>The Data Transfer wizard's steps, refusals and run (data-transfer/01).</summary>
public sealed class DataTransferViewModelTests : DumpTestDatabases
{
    [Fact]
    public async Task WalksSourceTargetTablesAndRunsTheTransfer()
    {
        var source = await SessionAsync("SQLite", "source", """
            CREATE TABLE a (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO a VALUES (1, 'x'), (2, 'y');
            CREATE TABLE b (id INTEGER PRIMARY KEY); INSERT INTO b VALUES (1);
            """);
        var target = await SessionAsync("SQLite", "target", null);
        var vm = new DataTransferViewModel(new ObservableCollection<DatabaseSession> { source, target });

        Assert.False(vm.NextCommand.CanExecute(null));
        vm.SourceSession = source;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsTargetStep);

        vm.TargetSession = source;
        Assert.Contains("same database", vm.TargetRefusal);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.TargetSession = target;
        Assert.Equal("", vm.TargetRefusal);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsObjectsStep);
        Assert.Equal(["main.a", "main.b"], vm.Tables.Select(t => t.Name));
        Assert.All(vm.Tables, t => Assert.True(t.IsSelected));
        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.Tables[0].IsSelected = true;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsOptionsStep);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsPreviewStep);
        Assert.Contains("CREATE TABLE", vm.PreviewScript, StringComparison.OrdinalIgnoreCase);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunStep);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.StartsWith("Created · 2 rows", vm.Tables[0].Result);
        Assert.Equal("", vm.Tables[1].Result);
        Assert.Equal("Transferred 1 of 1 table · 2 rows.", vm.Summary);
        Assert.Equal("1|x\n2|y", await QueryAsync(target, "SELECT * FROM a ORDER BY id"));
        Assert.Equal("", await QueryAsync(target, "SELECT name FROM sqlite_master WHERE name = 'b'"));
    }

    [Fact]
    public async Task ADifferentProviderIsAcceptedOnTheTargetStep()
    {
        var source = await SessionAsync("SQLite", "source", "CREATE TABLE a (id INTEGER PRIMARY KEY);");
        var duck = Session("DuckDB", "duck");
        var vm = new DataTransferViewModel(new ObservableCollection<DatabaseSession> { source, duck }) { SourceSession = source };
        await vm.NextCommand.ExecuteAsync(null);

        vm.TargetSession = duck;

        Assert.Equal("", vm.TargetRefusal);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void OutcomesAreDescribedWithTheirReason()
    {
        var table = new DatabaseObjectIdentity(null, "main", "t", SchemaNodeType.Table);
        Assert.Equal("Failed: Row 3 was rejected: boom. The table's changes were rolled back.",
            DataTransferViewModel.Describe(new(table, DataTransferTableStatus.Failed, 0, TimeSpan.Zero,
                "Row 3 was rejected: boom. The table's changes were rolled back.")));
        Assert.Equal("Transferred 1 of 2 tables · 100 rows · 1 canceled.", DataTransferViewModel.Summarize(
        [
            new(table, DataTransferTableStatus.Appended, 100, TimeSpan.Zero),
            new(table, DataTransferTableStatus.Canceled, 0, TimeSpan.Zero, "Not started.")
        ]));
    }
}
