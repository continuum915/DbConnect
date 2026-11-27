using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Batch sizing, transaction modes and continue-on-error for data transfer (data-transfer/02).</summary>
public sealed class DataTransferBatchTests : DumpTestDatabases
{
    private const string Customers = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO customers VALUES (1, 'Ann'), (2, 'Bo'), (3, 'Cy'), (4, 'Di'), (5, 'Ed');
        """;

    // Row 4 of the source collides with the target's own row 4.
    private const string TargetWithRow4 = "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL); INSERT INTO customers VALUES (4, 'kept');";

    private static IReadOnlyList<DatabaseObjectIdentity> CustomersTable => [Table("customers")];

    [Fact]
    public void StatementsStayWithinTheParameterAndValuesLimits()
    {
        var narrow = new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { MaxCommandParameters = 10 };
        var insert = new DataTransferInsert(narrow, "t", ["a", "b", "c"], 100);
        var rows = Enumerable.Range(0, 7).Select(i => (IReadOnlyList<object?>)[i, i, i]).ToList();

        var commands = insert.Commands(rows);

        Assert.Equal(3, insert.RowsPerStatement);
        Assert.Equal([9, 9, 3], commands.Select(c => c.Parameters.Count));
        Assert.All(commands, c => Assert.True(c.Parameters.Count <= 10));
        Assert.Equal("INSERT INTO t (\"a\", \"b\", \"c\") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8)", commands[0].CommandText);

        // SQL Server: a VALUES list holds at most 1,000 rows, and a command binds at most 2,100 parameters.
        var sqlServer = new DatabaseService().CreateSession(new ConnectionInfo { Name = "s", Provider = "SQL Server", ConnectionString = "Server=x" }).Capabilities;
        Assert.Equal(1000, new DataTransferInsert(sqlServer, "t", ["a"], 5000).RowsPerStatement);
        Assert.Equal(700, new DataTransferInsert(sqlServer, "t", ["a", "b", "c"], 5000).RowsPerStatement);
        Assert.Throws<DataTransferException>(() => new DataTransferInsert(narrow, "t", Enumerable.Range(0, 11).Select(i => $"c{i}").ToList(), 1));
    }

    [Fact]
    public async Task AWideTableIsSplitBelowSqlitesParameterLimit()
    {
        // 100 columns x 400 rows is 40,000 parameters in one batch; SQLite binds at most 32,766 per command.
        var columns = string.Join(", ", Enumerable.Range(0, 100).Select(i => $"c{i} INTEGER"));
        var values = string.Join(", ", Enumerable.Range(1, 400).Select(r => "(" + string.Join(", ", Enumerable.Range(0, 100).Select(c => r * 1000 + c)) + ")"));
        var source = await SessionAsync("SQLite", "source", $"CREATE TABLE wide ({columns}); INSERT INTO wide VALUES {values};");
        var target = await SessionAsync("SQLite", "target", null);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Table("wide")], new DataTransferOptions { BatchSize = 400 }));

        Assert.True(outcome.Status == DataTransferTableStatus.Created, outcome.Diagnostic);
        Assert.Equal(400, outcome.RowsCopied);
        Assert.Equal(await QueryAsync(source, "SELECT * FROM wide ORDER BY c0"), await QueryAsync(target, "SELECT * FROM wide ORDER BY c0"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task PerTableOneRejectedRowUndoesTheTable(string provider)
    {
        var source = await SessionAsync(provider, "source", Customers);
        var target = await SessionAsync(provider, "target", TargetWithRow4);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, CustomersTable,
            new DataTransferOptions { BatchSize = 2, ContinueOnError = true }));

        // Continue-on-error does nothing per table: the rejected row undoes the table.
        Assert.Equal(DataTransferTableStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.RowsCopied);
        Assert.Contains("One of rows 3–4 was rejected", outcome.Diagnostic);
        Assert.Equal("4|kept", await QueryAsync(target, "SELECT * FROM customers"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task PerBatchAFailureKeepsTheBatchesBeforeIt(string provider)
    {
        var source = await SessionAsync(provider, "source", Customers);
        var target = await SessionAsync(provider, "target", TargetWithRow4);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, CustomersTable,
            new DataTransferOptions { BatchSize = 2, Transaction = DataTransferTransactionMode.PerBatch }));

        Assert.Equal(DataTransferTableStatus.Failed, outcome.Status);
        Assert.Equal(2, outcome.RowsCopied);
        Assert.Contains("2 rows from earlier batches were kept", outcome.Diagnostic);
        Assert.Equal("1|Ann\n2|Bo\n4|kept", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task PerBatchWithContinueOnErrorSkipsOnlyTheRejectedRow(string provider)
    {
        var source = await SessionAsync(provider, "source", Customers);
        var target = await SessionAsync(provider, "target", TargetWithRow4);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, CustomersTable, new DataTransferOptions
        {
            BatchSize = 2,
            Transaction = DataTransferTransactionMode.PerBatch,
            ContinueOnError = true
        }));

        Assert.Equal(DataTransferTableStatus.Appended, outcome.Status);
        Assert.Equal(4, outcome.RowsCopied);
        Assert.Equal(1, outcome.RowsSkipped);
        var skipped = Assert.Single(outcome.Skipped);
        Assert.Equal(4, skipped.RowNumber);
        Assert.Equal("id=4", skipped.Key);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Error));
        Assert.Equal("1|Ann\n2|Bo\n3|Cy\n4|kept\n5|Ed", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Fact]
    public async Task PerBatchACanceledTableKeepsItsCreatedTableAndCommittedBatches()
    {
        var source = await SessionAsync("SQLite", "source", Customers);
        var target = await SessionAsync("SQLite", "target", null);
        using var cancellation = new CancellationTokenSource();
        var progress = new Inline(p => { if (p.Rows >= 2) cancellation.Cancel(); });

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, CustomersTable,
            new DataTransferOptions { BatchSize = 2, Transaction = DataTransferTransactionMode.PerBatch }, progress, cancellation.Token));

        Assert.Equal(DataTransferTableStatus.Canceled, outcome.Status);
        Assert.Equal(2, outcome.RowsCopied);
        Assert.Contains("The created table stays on the target with 2 rows", outcome.Diagnostic);
        Assert.Equal("1|Ann\n2|Bo", await QueryAsync(target, "SELECT * FROM customers ORDER BY id"));
    }

    [Fact]
    public async Task TheWizardOffersContinueOnErrorPerBatchAndListsSkippedRows()
    {
        var source = await SessionAsync("SQLite", "source", Customers);
        var target = await SessionAsync("SQLite", "target", TargetWithRow4);
        var vm = new DataTransferViewModel(new ObservableCollection<DatabaseSession> { source, target })
        {
            SourceSession = source
        };
        await vm.NextCommand.ExecuteAsync(null);
        vm.TargetSession = target;
        Assert.Equal("", vm.TargetWarning);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsOptionsStep);
        Assert.False(vm.CanContinueOnError);
        vm.Transaction = DataTransferViewModel.TransactionChoices.Single(c => c.Mode == DataTransferTransactionMode.PerBatch);
        Assert.True(vm.CanContinueOnError);
        vm.ContinueOnError = true;
        vm.BatchSize = 2;
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("1 rows skipped", vm.Tables[0].Result);
        Assert.Contains("skipped row 4 (id=4):", vm.Report);
        Assert.Contains("Transferred 1 of 1 table · 4 rows · 1 rows skipped.", vm.Summary);
    }

    [Fact]
    public void ATargetWhoseDdlCommitsAtOnceIsWarnedAbout()
    {
        var mysql = new DatabaseService().CreateSession(new ConnectionInfo { Name = "m", Provider = "MySQL", ConnectionString = "Server=x" });
        var vm = new DataTransferViewModel([]) { TargetSession = mysql };

        Assert.Contains("MySQL commits CREATE TABLE", vm.TargetWarning);
    }

    private sealed class Inline(Action<DataTransferProgress> report) : IProgress<DataTransferProgress>
    {
        public void Report(DataTransferProgress value) => report(value);
    }
}
