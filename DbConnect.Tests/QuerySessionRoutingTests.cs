using System;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class QuerySessionRoutingTests : MainWindowTestBase
{
    [Fact]
    public async Task ExecuteQueryAsync_UsesSelectedQueryTabSession()
    {
        var firstProvider = new RoutingProvider("First");
        var secondProvider = new RoutingProvider("Second");
        var dbService = new DatabaseService();
        var firstSession = CreateSession("First Connection", firstProvider);
        var secondSession = CreateSession("Second Connection", secondProvider);
        var vm = CreateMainWindowViewModel(dbService);
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(firstSession);
        vm.ActiveSessionOptions.Add(secondSession);
        tab.SelectedSession = secondSession;
        tab.QueryText = "SELECT 42;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Null(firstProvider.LastQuery);
        Assert.Equal("SELECT 42", secondProvider.LastQuery);
        var result1 = tab.CurrentResult!;
        Assert.Equal("Second: SELECT 42", result1.ObjectRows.Single()[result1.IndexOfColumn("Provider")]);
    }

    [Fact]
    public async Task ExecuteSelectionAsync_UsesOnlySelectedText()
    {
        var provider = new RoutingProvider("Selected");
        var session = CreateSession("Selected Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT 1; SELECT 2; ";
        tab.SelectedQueryText = " SELECT 2; ";
        tab.SelectedQueryStart = tab.QueryText.IndexOf(" SELECT 2;", StringComparison.Ordinal);

        await vm.ExecuteSelectionCommand.ExecuteAsync(null);

        Assert.Equal(["SELECT 2"], provider.ExecutedQueries);
        Assert.Equal(" SELECT 2; ", provider.LastQuery);
        Assert.Equal(" SELECT 2; ", tab.CurrentResult?.ExecutedSql);
        Assert.Equal(QueryExecutionScope.Selection, tab.CurrentResult?.ExecutionScope);
    }

    [Fact]
    public async Task ExecuteSelectionAsync_WhitespaceSelectionDoesNotRunStatementAtCaret()
    {
        var provider = new RoutingProvider("Selected");
        var session = CreateSession("Selected Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT 1;   SELECT 2;";
        tab.SelectedQueryText = "   ";
        tab.SelectedQueryStart = tab.QueryText.IndexOf("   ", StringComparison.Ordinal);
        tab.CaretOffset = tab.QueryText.IndexOf("SELECT 2", StringComparison.Ordinal);

        await vm.ExecuteSelectionCommand.ExecuteAsync(null);

        Assert.Empty(provider.ExecutedQueries);
        Assert.True(tab.CurrentResult?.HasError);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ExecutesBatchStatementsSequentially()
    {
        var provider = new RoutingProvider("Batch");
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        // This test owns the timeout; asynchronous workspace settings must not overwrite it.
        vm.Tabs.Clear();
        var tab = new QueryTabViewModel();
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.TimeoutSeconds = 60;
        tab.QueryText = "SELECT 1; UPDATE Demo SET Value = 2;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal(["SELECT 1", "UPDATE Demo SET Value = 2"], provider.ExecutedQueries);
        Assert.Equal([60, 60], provider.Timeouts);
        Assert.Equal(2, tab.CurrentResult?.StatementCount);
        Assert.Equal(2, tab.CurrentResult?.Statements.Count);
        Assert.Collection(tab.Results,
            first =>
            {
                Assert.Equal("SELECT 1", first.ExecutedSql);
                Assert.Equal(0, first.ReturnedStatementIndex);
                Assert.Equal("Statement 1", first.TabLabel);
            },
            second =>
            {
                Assert.Equal("UPDATE Demo SET Value = 2", second.ExecutedSql);
                Assert.Equal(1, second.ReturnedStatementIndex);
                Assert.Equal("Statement 2", second.TabLabel);
            });
    }

    [Fact]
    public async Task ExecuteQueryAsync_AttributesNoticesToTheProducingStatement()
    {
        var provider = new RoutingProvider("Batch");
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "UPDATE Demo SET Value = 1; UPDATE WARN SET Value = 2;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Collection(tab.MessagesRows,
            first => Assert.Equal(string.Empty, first.Warnings),
            second => Assert.Equal("simulated warning", second.Warnings));
    }

    [Fact]
    public async Task ExecuteQueryAsync_SumsRowsAcrossResultSetsOfOneStatement()
    {
        var provider = new RoutingProvider("Batch");
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT MULTI_RESULTS;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Single(tab.MessagesRows);
        Assert.Equal("3 returned", tab.MessagesRows[0].Rows);
        Assert.Equal(2, tab.Results.Count);
    }

    [Fact]
    public async Task ExecuteQueryAsync_KeepsNoticesWhenStatementFails()
    {
        var provider = new RoutingProvider("Batch");
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "ERROR WARN;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal("simulated warning", Assert.Single(tab.MessagesRows).Warnings);
        Assert.Equal("Simulated batch failure.", tab.MessagesRows[0].Error);
    }

    [Fact]
    public async Task ExecuteQueryAsync_HighlightsEachStatementAndClearsAfterCompletion()
    {
        var reachedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RoutingProvider("Batch")
        {
            BeforeExecute = async sql =>
            {
                if (sql.Contains("second_value", StringComparison.Ordinal))
                {
                    reachedSecond.SetResult();
                    await releaseSecond.Task;
                }
            }
        };
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT first_value;\n  SELECT second_value;";

        var execution = vm.ExecuteQueryCommand.ExecuteAsync(null);
        await reachedSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var span = Assert.IsType<StatementSpan>(tab.RunningStatementSpan);
        Assert.Equal("SELECT second_value", tab.QueryText.Substring(span.Start, span.Length));

        releaseSecond.SetResult();
        await execution;
        Assert.Null(tab.RunningStatementSpan);
        Assert.Equal(["SELECT first_value", "SELECT second_value"], provider.ExecutedQueries);
    }

    [Fact]
    public async Task ExecuteQueryAsync_CancelledDuringPreparationSubmitsNothingAndShowsCancellation()
    {
        var provider = new RoutingProvider("Batch");
        var session = CreateSession("Batch Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT 1; SELECT 2;";
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tab.IsExecuting) && tab.IsExecuting)
                tab.CancelExecutionCommand.Execute(null);
        };

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Empty(provider.ExecutedQueries);
        Assert.True(tab.CurrentResult?.Error?.IsCanceled);
        Assert.False(tab.IsExecuting);
        Assert.False(vm.IsExecuting);
        Assert.Null(tab.RunningStatementSpan);
    }

    [Fact]
    public async Task ExecuteSelectionAsync_HighlightsSelectionAtItsEditorOffset()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RoutingProvider("Selected")
        {
            BeforeExecute = async _ =>
            {
                started.SetResult();
                await release.Task;
            }
        };
        var session = CreateSession("Selected Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT first_value;\n SELECT second_value;";
        tab.SelectedQueryStart = tab.QueryText.IndexOf("SELECT second_value", StringComparison.Ordinal);
        tab.SelectedQueryText = "SELECT second_value;";

        var execution = vm.ExecuteSelectionCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(tab.SelectedQueryStart, tab.RunningStatementSpan?.Start);

        release.SetResult();
        await execution;
        Assert.Equal(["SELECT second_value"], provider.ExecutedQueries);
    }

    [Fact]
    public async Task ExecuteQueryAsync_RoutesLastRowReturningStatementInMixedBatch()
    {
        var provider = new RoutingProvider("Rows");
        var session = CreateSession("Rows Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT first_value; SELECT second_value;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal(1, tab.CurrentResult?.ReturnedStatementIndex);
        var result2 = tab.CurrentResult!;
        Assert.Equal("Rows: SELECT second_value", result2.ObjectRows.Single()[result2.IndexOfColumn("Provider")]);
        Assert.Contains("result from statement #2", tab.ResultSummary);
    }

    [Fact]
    public async Task ExecuteQueryAsync_RoutesSelectAfterDml()
    {
        var provider = new RoutingProvider("Mixed");
        var session = CreateSession("Mixed Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "UPDATE Demo SET Value = 2; SELECT final_value;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal(1, tab.CurrentResult?.ReturnedStatementIndex);
        var result3 = tab.CurrentResult!;
        Assert.Equal("Mixed: SELECT final_value", result3.ObjectRows.Single()[result3.IndexOfColumn("Provider")]);
    }

    [Fact]
    public async Task ExecuteQueryAsync_RoutesLastSelectBeforeTrailingDml()
    {
        var provider = new RoutingProvider("Mixed");
        var session = CreateSession("Mixed Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT current_value; UPDATE Demo SET Value = 2;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal(0, tab.CurrentResult?.ReturnedStatementIndex);
        var result4 = tab.CurrentResult!;
        Assert.Equal("Mixed: SELECT current_value", result4.ObjectRows.Single()[result4.IndexOfColumn("Provider")]);
    }

    [Fact]
    public async Task ExecuteQueryAsync_RoutesFailedStatementIndexWhenBatchErrors()
    {
        var provider = new RoutingProvider("Error");
        var session = CreateSession("Error Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT ok_value; ERROR boom; SELECT never_reached;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(tab.CurrentResult?.HasError);
        Assert.Equal(1, tab.CurrentResult?.ReturnedStatementIndex);
        Assert.Equal(2, tab.CurrentResult?.Statements.Count);
        Assert.Contains("#2", tab.BatchStatementSummary);
    }

    [Fact]
    public async Task ExecuteQueryAsync_FailedMiddleStatement_NavigatesToErrorToken()
    {
        var provider = new RoutingProvider("Error");
        var session = CreateSession("Error Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT ok_value; ERROR boom; SELECT never_reached;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.Equal(new StatementSpan(23, 4), tab.ErrorSpan);
        Assert.Equal(17, tab.CurrentResult?.StatementStart);
    }

    [Fact]
    public async Task ExecuteQueryAsync_PreservesEmptyResultSetRouting()
    {
        var provider = new RoutingProvider("Empty");
        var session = CreateSession("Empty Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT empty_result;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(tab.CurrentResult?.HasResultSet);
        Assert.False(tab.CurrentResult?.HasRows);
        Assert.Equal(0, tab.CurrentResult?.ReturnedStatementIndex);
        Assert.Contains("0 row(s)", tab.ResultSummary);
    }

    [Fact]
    public void ScriptTableAsSelect_SelectsOwningTableSession()
    {
        var provider = new RoutingProvider("Owner");
        var session = CreateSession("Owner Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        root.Children.Add(table);
        vm.ActiveSessions[root] = session;
        vm.ActiveSessionOptions.Add(session);

        vm.ScriptTableAsSelect(table);

        var tab = Assert.IsType<QueryTabViewModel>(vm.SelectedTab);
        Assert.Same(session, tab.SelectedSession);
        Assert.Equal("SELECT * FROM \"Customers\" LIMIT 100;", tab.QueryText);
    }

    [Fact]
    public void Disconnect_RemovesSessionSchemaIndexesAndOwnedTabs()
    {
        var provider = new RoutingProvider("Owner");
        var session = CreateSession("Owner Connection", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var queryTab = vm.NewQueryTabWithSession();
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        root.Children.Add(table);
        vm.SchemaNodes.Add(root);
        vm.ActiveConnections[root] = session.Connection;
        vm.ActiveSessions[root] = session;
        vm.ActiveIndexes[root] = new SchemaIndex { Provider = session.ProviderName };
        vm.ActiveSessionOptions.Add(session);
        queryTab.SelectedSession = session;
        var tableTab = new TableDataTabViewModel("Customers", session, table);
        vm.Tabs.Add(tableTab);
        vm.SelectedTab = tableTab;

        vm.Disconnect(root);

        Assert.Empty(vm.SchemaNodes);
        Assert.Empty(vm.ActiveConnections);
        Assert.Empty(vm.ActiveSessions);
        Assert.Empty(vm.ActiveIndexes);
        Assert.Empty(vm.ActiveSessionOptions);
        Assert.DoesNotContain(tableTab, vm.Tabs);
        Assert.Null(queryTab.SelectedSession);
        Assert.Same(queryTab, vm.SelectedTab);
        Assert.Equal("Disconnected Owner Connection.", vm.StatusMessage);
    }

    private static DatabaseSession CreateSession(string name, IDatabaseProvider provider) =>
        new(
            new ConnectionInfo
            {
                Name = name,
                Provider = provider.ProviderName,
                ConnectionString = $"{name} connection string"
            },
            provider);

    private sealed class RoutingProvider : IDatabaseProvider
    {
        private readonly string _resultName;

        public RoutingProvider(string resultName)
        {
            _resultName = resultName;
            ProviderName = resultName;
        }

        public string ProviderName { get; }
        public string ProviderIcon => "R";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT")
        {
            TransactionSupport = TransactionSupportLevel.Guaranteed
        };
        public string ExplainPrefix => "EXPLAIN";
        public string? LastQuery { get; private set; }
        public List<string> ExecutedQueries { get; } = [];
        public List<int> Timeouts { get; } = [];
        public Func<string, Task>? BeforeExecute { get; init; }

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult(_resultName);
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            if (BeforeExecute != null)
                await BeforeExecute(query);
            yield return await ExecuteQueryAsync(connectionString, query, options);
            if (query.Contains("MULTI_RESULTS", StringComparison.OrdinalIgnoreCase))
                yield return new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "Other", Ordinal = 0 }],
                    ObjectRows = [new object?[] { 1 }, new object?[] { 2 }],
                    Duration = TimeSpan.FromMilliseconds(10)
                };
        }

        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null)
        {
            LastQuery = query;
            var normalizedQuery = query.Trim().TrimEnd(';');
            ExecutedQueries.Add(normalizedQuery);
            Timeouts.Add(options?.TimeoutSeconds ?? 0);
            if (normalizedQuery.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new QueryResult
                {
                    ExecutedSql = query,
                    Duration = TimeSpan.FromMilliseconds(10),
                    Messages = normalizedQuery.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                        ? ["simulated warning"] : [],
                    Error = new QueryErrorInfo
                    {
                        Provider = ProviderName,
                        Summary = "Simulated batch failure.",
                        Detail = normalizedQuery,
                        Position = new QueryErrorPosition(Offset: 6)
                    }
                });
            }

            if (normalizedQuery.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var isEmpty = normalizedQuery.Contains("empty_result", StringComparison.OrdinalIgnoreCase);
                var result = new QueryResult
                {
                    ExecutedSql = query,
                    Duration = TimeSpan.FromMilliseconds(10),
                    ColumnInfos = [new ColumnInfo { Name = "Provider", Ordinal = 0 }]
                };
                if (!isEmpty)
                {
                    result.ObjectRows =
                    [
                        new object?[] { $"{_resultName}: {normalizedQuery}" }
                    ];
                }

                return Task.FromResult(result);
            }

            return Task.FromResult(new QueryResult
            {
                ExecutedSql = query,
                Duration = TimeSpan.FromMilliseconds(10),
                RecordsAffected = 1,
                Messages = normalizedQuery.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                    ? ["simulated warning"] : []
            });
        }

        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult { PageIndex = pageIndex, PageSize = pageSize });

        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(1);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(statements.Count));
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(commands.Count));
        public string FormatLimitQuery(string query, int limit) => $"{query.TrimEnd(';')} LIMIT {limit};";
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => ["text"];
    }
}
