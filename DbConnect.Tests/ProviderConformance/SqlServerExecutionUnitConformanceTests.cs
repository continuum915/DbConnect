using System.Diagnostics;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>What a script does on a real server when Run sends one command per GO batch.</summary>
public sealed class SqlServerExecutionUnitConformanceTests : MainWindowTestBase, IAsyncDisposable
{
    private readonly SqlServerProvider _provider = new();
    private readonly string _table = "unit_" + Guid.NewGuid().ToString("N");
    private readonly string _procedure = "unit_proc_" + Guid.NewGuid().ToString("N");
    private readonly string _view = "unit_view_" + Guid.NewGuid().ToString("N");
    private static string ConnectionString => ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server")!;

    public async ValueTask DisposeAsync()
    {
        if (!SqlServerExportEnvironment.IsConfigured) return;
        await _provider.ExecuteNonQueryAsync(ConnectionString, $"DROP TABLE IF EXISTS {_table}; DROP PROCEDURE IF EXISTS {_procedure}; DROP VIEW IF EXISTS {_view};");
    }

    private async Task CreateTableAsync() =>
        await _provider.ExecuteNonQueryAsync(ConnectionString, $"CREATE TABLE {_table} (id int PRIMARY KEY)");

    private async Task<int[]> RowsAsync()
    {
        var rows = await _provider.ExecuteQueryAsync(ConnectionString, $"SELECT id FROM {_table} ORDER BY id");
        Assert.False(rows.HasError, rows.ErrorMessage);
        return rows.ObjectRows.Select(row => Convert.ToInt32(row[0])).ToArray();
    }

    private (MainWindowViewModel Main, QueryTabViewModel Tab) CreateTab(string sql)
    {
        var main = CreateMainWindowViewModel(new DatabaseService());
        var tab = main.NewQueryTabWithSession();
        var session = new DatabaseSession(new ConnectionInfo { Provider = _provider.ProviderName, ConnectionString = ConnectionString }, _provider);
        main.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = sql;
        return (main, tab);
    }

    private async Task<QueryTabViewModel> RunAsync(string sql)
    {
        var (main, tab) = CreateTab(sql);
        await main.ExecuteQueryCommand.ExecuteAsync(null);
        return tab;
    }

    private static object? Scalar(QueryResult result) => Assert.Single(result.ObjectRows)[0];

    [SqlServerExportFact]
    public async Task AVariableIsVisibleAcrossSemicolonsInItsBatch()
    {
        var tab = await RunAsync("DECLARE @x int = 7; SELECT @x AS v;\nGO\nSELECT 2 AS w;");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal(7, Scalar(tab.Results[0]));
        Assert.Equal(2, Scalar(tab.Results[1]));
    }

    [SqlServerExportFact]
    public async Task AVariableStaysLocalToItsBatch()
    {
        var tab = await RunAsync("DECLARE @x int = 7;\nGO\nSELECT @x AS v;");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Contains("@x", tab.CurrentResult.ErrorMessage);
    }

    [SqlServerExportFact]
    public async Task AProcedureKeepsEveryStatementOfItsBody()
    {
        var tab = await RunAsync($"CREATE PROCEDURE {_procedure} AS SELECT 1 AS a; SELECT 2 AS b;\nGO\nEXEC {_procedure};");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal([1, 2], tab.Results.Where(result => result.HasResultSet).Select(result => Convert.ToInt32(Scalar(result))));
    }

    [SqlServerExportFact]
    public async Task AViewCanBeCreatedAfterALeadingComment()
    {
        var tab = await RunAsync($"-- note\nCREATE VIEW {_view} AS SELECT 1 AS a;\nGO\nSELECT a FROM {_view};");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal(1, Scalar(tab.CurrentResult));
    }

    [SqlServerExportFact]
    public async Task AMergeKeepsItsRequiredTerminator()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"MERGE INTO {_table} AS t USING (SELECT 1 AS id) AS s ON t.id = s.id WHEN NOT MATCHED THEN INSERT (id) VALUES (s.id);");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal(new[] { 1 }, await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task ResultsBeforeAFailureInTheBatchAreKept()
    {
        var tab = await RunAsync("SELECT 1 AS a; SELECT * FROM missing_table_for_unit_test;");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Contains("missing_table_for_unit_test", tab.CurrentResult.ErrorMessage);
        Assert.Contains(tab.Results, result => result.HasResultSet && Convert.ToInt32(Scalar(result)) == 1);
    }

    [SqlServerExportFact]
    public async Task AGoCountRunsItsBatchThatManyTimes()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"DECLARE @next int = (SELECT ISNULL(MAX(id), 0) + 1 FROM {_table}); INSERT {_table} VALUES (@next);\nGO 3\nSELECT COUNT(*) AS n FROM {_table};");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal(new[] { 1, 2, 3 }, await RowsAsync());
        Assert.Equal(3, Scalar(tab.CurrentResult));
    }

    [SqlServerExportFact]
    public async Task AFailureStopsTheRemainingRepeatsAndLaterBatches()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"INSERT {_table} VALUES (1);\nGO 3\nINSERT {_table} VALUES (2);");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Equal("2627", tab.CurrentResult.Error!.Code);
        Assert.Equal(new[] { 1 }, await RowsAsync());
    }

    [SqlServerExportTheory]
    [InlineData("{0}\nGO 2")]
    [InlineData("{0}\nGO\n{0}")]
    public async Task ARepeatedTypedCommitBehavesLikeTheBatchWrittenOutAgain(string script)
    {
        await CreateTableAsync();
        var batch = $"INSERT {_table} SELECT ISNULL(MAX(id), 0) + 1 FROM {_table}; COMMIT;";
        var (main, tab) = CreateTab(string.Format(script, batch));
        tab.IsManualCommit = true;

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        // The first COMMIT ends the transaction manual commit opened, and the next one opens at the next Run.
        Assert.Contains("no corresponding BEGIN TRANSACTION", tab.CurrentResult!.ErrorMessage);
        Assert.False(tab.IsInTransaction);
        Assert.Equal(new[] { 1, 2 }, await RowsAsync());
    }

    [SqlServerExportTheory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task AnInvalidGoCountFailsWithoutRunningItsBatch(string count)
    {
        await CreateTableAsync();

        var tab = await RunAsync($"INSERT {_table} VALUES (1);\nGO {count}");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Empty(await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task AFailureFollowsSqlServerRulesInItsBatchAndStopsLaterBatches()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"INSERT {_table} VALUES (1); INSERT {_table} VALUES (1); INSERT {_table} VALUES (2);\nGO\nINSERT {_table} VALUES (3);");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Equal("2627", tab.CurrentResult.Error!.Code);
        // The server continues after a statement-level error in the same batch, as sqlcmd and SSMS show.
        Assert.Equal(new[] { 1, 2 }, await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task ASyntaxErrorStopsItsWholeBatchBeforeAnyOfItRuns()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"INSERT {_table} VALUES (1); SELECT FROM;");

        Assert.True(tab.CurrentResult!.HasError);
        Assert.Empty(await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task TheScriptsOwnTransactionWorksWithinOneBatch()
    {
        await CreateTableAsync();

        var tab = await RunAsync($"DECLARE @id int = 4; BEGIN TRAN; INSERT {_table} VALUES (@id); COMMIT;");

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.False(tab.IsInTransaction);
        Assert.Equal(new[] { 4 }, await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task ATypedCommitInManualCommitModeStillEndsTheSessionTransaction()
    {
        await CreateTableAsync();
        var (main, tab) = CreateTab($"INSERT {_table} VALUES (1); COMMIT;\nGO\nINSERT {_table} VALUES (2);");
        tab.IsManualCommit = true;

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.False(tab.IsInTransaction);
        Assert.Equal(new[] { 1, 2 }, await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task ManualCommitHoldsABatchUntilCommit()
    {
        await CreateTableAsync();
        var (main, tab) = CreateTab($"DECLARE @id int = 5; INSERT {_table} VALUES (@id);");
        tab.IsManualCommit = true;

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.True(tab.IsInTransaction);
        await tab.RollbackCommand.ExecuteAsync(null);
        Assert.Empty(await RowsAsync());
    }

    [SqlServerExportFact]
    public async Task AnErrorInALaterBatchHighlightsItsLineInTheEditor()
    {
        const string sql = "SELECT 1 AS a\nGO\nSELECT 2 AS b;\nSELECT FROM missing;";

        var tab = await RunAsync(sql);

        Assert.True(tab.CurrentResult!.HasError);
        Assert.NotNull(tab.ErrorSpan);
        Assert.Equal(sql.LastIndexOf("SELECT", StringComparison.Ordinal), tab.ErrorSpan!.Value.Start);
    }

    [SqlServerExportFact]
    public async Task CancellingStopsARunningBatch()
    {
        var (main, tab) = CreateTab("SELECT 1 AS a; WAITFOR DELAY '00:00:30'; SELECT 2 AS b;");
        var clock = Stopwatch.StartNew();

        var run = main.ExecuteQueryCommand.ExecuteAsync(null);
        await Task.Delay(1000);
        tab.CancelExecutionCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 20);
        Assert.True(tab.CurrentResult!.Error?.IsCanceled);
    }

    [SqlServerExportFact]
    public async Task TheTimeoutAppliesPerStatementNotToTheWholeBatch()
    {
        var (main, tab) = CreateTab("WAITFOR DELAY '00:00:00.7'; WAITFOR DELAY '00:00:00.7'; WAITFOR DELAY '00:00:00.7'; SELECT 1 AS a;");
        tab.TimeoutSeconds = 1;

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.False(tab.CurrentResult!.HasError, tab.CurrentResult.ErrorMessage);
        Assert.Equal(1, Scalar(tab.CurrentResult));
    }

    [SqlServerExportFact]
    public async Task ASingleStatementStillTimesOut()
    {
        var (main, tab) = CreateTab("WAITFOR DELAY '00:00:10';");
        tab.TimeoutSeconds = 1;
        var clock = Stopwatch.StartNew();

        await main.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(tab.CurrentResult!.Error?.IsTimeout);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 8);
    }

    [SqlServerExportFact]
    public async Task RunningTheStatementAtTheCaretStillRunsOneStatement()
    {
        const string sql = "SELECT 1 AS a; SELECT 2 AS b;";
        var (main, tab) = CreateTab(sql);
        tab.CaretOffset = sql.IndexOf("SELECT 2", StringComparison.Ordinal) + 2;
        tab.SelectedQueryText = "";

        await main.ExecuteSelectionCommand.ExecuteAsync(null);

        Assert.Equal(2, Scalar(Assert.Single(tab.Results)));
    }
}
