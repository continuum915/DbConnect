using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Target table modes and the confirmation before dropping or emptying target tables (data-transfer/03).</summary>
public sealed class DataTransferModeTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER);
        INSERT INTO customers VALUES (1, 'Ann', 1), (2, 'Bo', 2), (3, 'Cy', -3);
        CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT);
        INSERT INTO notes VALUES (1, 'n');
        """;

    // An older customers table on the target: a different definition, its own rows, and a CHECK that refuses row 3.
    private const string Target = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER CHECK (qty > 0), legacy TEXT);
        INSERT INTO customers VALUES (7, 'old', 7, 'x'), (8, 'older', 8, 'y');
        """;

    private static DatabaseObjectIdentity Customers => Table("customers");

    private static string Fixture(string provider, string sql) => provider == "DuckDB" ? sql.Replace(" TEXT", " VARCHAR") : sql;

    private async Task<(DatabaseSession Source, DatabaseSession Target)> DatabasesAsync(string provider) =>
        (await SessionAsync(provider, "source", Fixture(provider, Source)), await SessionAsync(provider, "target", Fixture(provider, Target)));

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task CreateFailsOnAnExistingTableWithoutTouchingItAndCreatesAMissingOne(string provider)
    {
        var (source, target) = await DatabasesAsync(provider);

        var outcomes = await DataTransferJob.RunAsync(source, target, [Customers, Table("notes")],
            new DataTransferOptions { Mode = DataTransferTargetMode.Create });

        Assert.Equal(DataTransferTableStatus.Failed, outcomes[0].Status);
        Assert.Equal("The target table already exists, so it was left unchanged.", outcomes[0].Diagnostic);
        Assert.Equal("7|old|7|x\n8|older|8|y", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
        Assert.Equal(DataTransferTableStatus.Created, outcomes[1].Status);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task DropAndRecreateReplacesTheDefinitionAndTheRows(string provider)
    {
        var (source, target) = await DatabasesAsync(provider);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Customers],
            new DataTransferOptions { Mode = DataTransferTargetMode.DropAndRecreate }));

        Assert.True(outcome.Status == DataTransferTableStatus.Recreated, outcome.Diagnostic);
        Assert.Equal(3, outcome.RowsCopied);
        Assert.Equal(await SnapshotAsync(source, "customers"), await SnapshotAsync(target, "customers"));
        Assert.Equal("1|Ann|1\n2|Bo|2\n3|Cy|-3", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task EmptyKeepsTheDefinitionAndReplacesTheRows(string provider)
    {
        var (source, target) = await DatabasesAsync(provider);
        await ExecuteScriptAsync(source, "DELETE FROM customers WHERE id = 3");
        var definition = await SnapshotAsync(target, "customers");

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Customers],
            new DataTransferOptions { Mode = DataTransferTargetMode.Empty }));

        Assert.True(outcome.Status == DataTransferTableStatus.Emptied, outcome.Diagnostic);
        Assert.Equal(definition, await SnapshotAsync(target, "customers"));
        Assert.Equal("1|Ann|1|∅\n2|Bo|2|∅", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task PerTableAFailedEmptyingPutsTheOldRowsBack(string provider)
    {
        var (source, target) = await DatabasesAsync(provider);

        // Row 3 breaks the target's CHECK after the old rows were removed.
        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Customers],
            new DataTransferOptions { Mode = DataTransferTargetMode.Empty }));

        Assert.Equal(DataTransferTableStatus.Failed, outcome.Status);
        Assert.Contains("rolled back", outcome.Diagnostic);
        Assert.Equal("7|old|7|x\n8|older|8|y", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Fact]
    public async Task PerBatchAFailedEmptyingSaysTheOldRowsAreGone()
    {
        var (source, target) = await DatabasesAsync("SQLite");

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Customers], new DataTransferOptions
        {
            Mode = DataTransferTargetMode.Empty,
            Transaction = DataTransferTransactionMode.PerBatch,
            BatchSize = 2
        }));

        Assert.Equal(DataTransferTableStatus.Failed, outcome.Status);
        Assert.Contains("The table's existing rows were removed.", outcome.Diagnostic);
        Assert.Contains("2 rows from earlier batches were kept", outcome.Diagnostic);
        Assert.Equal("1|Ann|1|∅\n2|Bo|2|∅", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Fact]
    public async Task ATableCanOverrideTheTransfersMode()
    {
        var (source, target) = await DatabasesAsync("SQLite");
        await ExecuteScriptAsync(target, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT); INSERT INTO notes VALUES (9, 'mine');");

        var outcomes = await DataTransferJob.RunAsync(source, target, [Customers, Table("notes")], new DataTransferOptions
        {
            Mode = DataTransferTargetMode.CreateOrAppend,
            TableModes = new Dictionary<DatabaseObjectIdentity, DataTransferTargetMode> { [Customers] = DataTransferTargetMode.DropAndRecreate }
        });

        Assert.Equal([DataTransferTableStatus.Recreated, DataTransferTableStatus.Appended], outcomes.Select(o => o.Status));
        Assert.Equal("1|n\n9|mine", await QueryAsync(target, "SELECT * FROM notes ORDER BY id"));
    }

    [Fact]
    public async Task DroppingOrEmptyingIsConfirmedWithTheAffectedTablesAndRefusedWithoutIt()
    {
        var (source, target) = await DatabasesAsync("SQLite");
        target.Connection.EnvironmentTier = "Production";
        var vm = new DataTransferViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source };
        await vm.NextCommand.ExecuteAsync(null);
        vm.TargetSession = target;
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        ((DataTransferTableChoice)vm.Tables.Single(t => t.Identity.Name == "customers")).Mode =
            DataTransferViewModel.TableModeChoices.Single(c => c.Mode == DataTransferTargetMode.Empty);
        DestructiveActionPreview? asked = null;
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunStep);
        vm.ConfirmDestructive = preview =>
        {
            asked = preview;
            return Task.FromResult(false);
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Equal("Production", asked.EnvironmentTier);
        Assert.Equal("target", asked.ObjectName);
        Assert.Equal("Empty \"main\".\"customers\" (if it exists)", asked.Sql);
        Assert.Equal("Not started: dropping or emptying target tables was not confirmed.", vm.Summary);
        Assert.Equal("7|old|7|x\n8|older|8|y", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
        Assert.Equal("", await QueryAsync(target, "SELECT name FROM sqlite_master WHERE name = 'notes'"));

        // The default mode drops and empties nothing, so it is not asked about.
        Assert.Null(DataTransferViewModel.DestructivePreview(target, [Customers], new DataTransferOptions()));
        vm.ConfirmDestructive = null;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Not started: dropping or emptying target tables was not confirmed.", vm.Summary);
    }
}
