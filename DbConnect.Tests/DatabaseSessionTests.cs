using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class DatabaseSessionTests : MainWindowTestBase
{
    [Fact]
    public async Task ExecuteTransactionAsync_DelegatesToProviderWithConnectionString()
    {
        var provider = new RecordingProvider();
        var connection = new ConnectionInfo
        {
            Name = "Local",
            Provider = provider.ProviderName,
            ConnectionString = "Data Source=:memory:"
        };
        var session = new DatabaseSession(connection, provider);

        var result = await session.ExecuteTransactionAsync(["UPDATE Customers SET Name = 'A';", "DELETE FROM Customers;"]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RecordsAffected);
        Assert.Equal(connection.ConnectionString, provider.LastTransactionConnectionString);
        Assert.Equal(["UPDATE Customers SET Name = 'A';", "DELETE FROM Customers;"], provider.LastTransactionStatements);
    }

    [Fact]
    public async Task LoadTablePageAsync_DelegatesToProviderWithPagingArguments()
    {
        var provider = new RecordingProvider
        {
            PageResult = new TablePageResult
            {
                PageIndex = 1,
                PageSize = 250,
                HasNextPage = true,
                Result = new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }],
                    ObjectRows = [new object?[] { 2 }]
                }
            }
        };
        var connection = new ConnectionInfo
        {
            Name = "Local",
            Provider = provider.ProviderName,
            ConnectionString = "Data Source=:memory:"
        };
        var session = new DatabaseSession(connection, provider);

        var result = await session.LoadTablePageAsync("Customers", ["Id"], 250, 1);

        Assert.Equal(connection.ConnectionString, provider.LastTablePageConnectionString);
        Assert.Equal("Customers", provider.LastTablePageTableName);
        Assert.Equal(["Id"], provider.LastTablePageOrderByColumns);
        Assert.Equal(250, provider.LastTablePagePageSize);
        Assert.Equal(1, provider.LastTablePagePageIndex);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void CreateSession_ResolvesProviderFromConnectionInfo()
    {
        var connection = new ConnectionInfo
        {
            Name = "Local",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        };

        var session = new DatabaseService().CreateSession(connection);

        Assert.Equal("SQLite", session.ProviderName);
        Assert.Equal(connection.ConnectionString, session.ConnectionString);
    }

    [Fact]
    public void OpenTableDataTab_KeepsSameTableNamesSeparateAcrossSessions()
    {
        var firstProvider = new RecordingProvider();
        var secondProvider = new RecordingProvider();
        var viewModel = CreateMainWindowViewModel(new DatabaseService());

        var firstRoot = new SchemaNode { Name = "First", Type = SchemaNodeType.Database };
        var firstTable = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        firstRoot.Children.Add(firstTable);

        var secondRoot = new SchemaNode { Name = "Second", Type = SchemaNodeType.Database };
        var secondTable = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        secondRoot.Children.Add(secondTable);

        viewModel.ActiveSessions[firstRoot] = new DatabaseSession(
            new ConnectionInfo
            {
                Name = "First",
                Provider = firstProvider.ProviderName,
                ConnectionString = "First connection"
            },
            firstProvider);
        viewModel.ActiveSessions[secondRoot] = new DatabaseSession(
            new ConnectionInfo
            {
                Name = "Second",
                Provider = secondProvider.ProviderName,
                ConnectionString = "Second connection"
            },
            secondProvider);

        viewModel.OpenTableDataTab(firstTable);
        viewModel.OpenTableDataTab(secondTable);

        Assert.Equal(2, viewModel.Tabs.OfType<TableDataTabViewModel>().Count());
    }

    [Fact]
    public async Task TableDataCommit_ExecutesDirtyStatementsThroughSessionTransaction()
    {
        var provider = new RecordingProvider
        {
            QueryResult = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1, "Old" }]
            }
        };
        var session = new DatabaseSession(
            new ConnectionInfo
            {
                Name = "Local",
                Provider = provider.ProviderName,
                ConnectionString = "Data Source=:memory:"
            },
            provider);
        var tableNode = new SchemaNode
        {
            Name = "Customers",
            RawName = "Customers",
            Type = SchemaNodeType.Table,
            Children =
            {
                new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true },
                new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column }
            }
        };
        var vm = new TableDataTabViewModel("Customers", session, tableNode);
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        var row = Assert.Single(vm.Rows);
        row.Values["Name"] = "New";
        vm.MarkRowModified(row);
        await vm.ExecuteCommitAsync();

        var command = Assert.Single(provider.LastTransactionCommands);
        Assert.Equal("""UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1 AND "Name" = @p2;""", command.CommandText);
        Assert.Equal("New", command.Parameters["@p0"]);
        Assert.Equal(1, command.Parameters["@p1"]);
        Assert.Equal("Old", command.Parameters["@p2"]);
    }

    [Theory]
    [InlineData(TransactionSupportLevel.Guaranteed, "Transactional commit")]
    [InlineData(TransactionSupportLevel.BestEffort, "Best-effort commit")]
    [InlineData(TransactionSupportLevel.None, "Non-transactional commit")]
    public async Task TableDataCommitPreview_UsesCapabilityTransactionMessaging(TransactionSupportLevel transactionSupport, string expectedLabel)
    {
        var provider = new RecordingProvider
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = transactionSupport
            },
            QueryResult = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1, "Old" }]
            }
        };
        var session = new DatabaseSession(
            new ConnectionInfo
            {
                Name = "Local",
                Provider = provider.ProviderName,
                ConnectionString = "Data Source=:memory:"
            },
            provider);
        var tableNode = new SchemaNode
        {
            Name = "Customers",
            RawName = "Customers",
            Type = SchemaNodeType.Table,
            Children =
            {
                new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true },
                new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column }
            }
        };
        var vm = new TableDataTabViewModel("Customers", session, tableNode);
        CommitPreviewRequest? preview = null;
        vm.CommitPreviewRequested += request => preview = request;

        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        var row = Assert.Single(vm.Rows);
        row.Values["Name"] = "New";
        vm.MarkRowModified(row);
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.NotNull(preview);
        Assert.Equal(expectedLabel, vm.CommitModeLabel);
        Assert.Equal(provider.Capabilities.TransactionCommitSummary, preview!.SummaryMessage);
    }

    [Fact]
    public async Task TableDataCommit_IncludesFailedStatementInStatus()
    {
        var provider = new RecordingProvider
        {
            QueryResult = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1, "Old" }]
            },
            ConfiguredTransactionResult = TransactionResult.Failure(
                """UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1;""",
                "constraint failed")
        };
        var session = new DatabaseSession(
            new ConnectionInfo
            {
                Name = "Local",
                Provider = provider.ProviderName,
                ConnectionString = "Data Source=:memory:"
            },
            provider);
        var tableNode = new SchemaNode
        {
            Name = "Customers",
            RawName = "Customers",
            Type = SchemaNodeType.Table,
            Children =
            {
                new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true },
                new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column }
            }
        };
        var vm = new TableDataTabViewModel("Customers", session, tableNode);
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        var row = Assert.Single(vm.Rows);
        row.Values["Name"] = "New";
        vm.MarkRowModified(row);
        await vm.ExecuteCommitAsync();

        Assert.Contains("constraint failed", vm.StatusMessage);
        Assert.Contains("""UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1;""", vm.StatusMessage);
    }

    private sealed class RecordingProvider : IDatabaseProvider
    {
        public string ProviderName => "Recording";
        public string ProviderIcon => "R";
        public DatabaseProviderCapabilities Capabilities { get; init; } = new("\"", "\"", "LIMIT")
        {
            TransactionSupport = TransactionSupportLevel.Guaranteed
        };
        public string ExplainPrefix => "EXPLAIN";
        public string? LastTransactionConnectionString { get; private set; }
        public IReadOnlyList<string> LastTransactionStatements { get; private set; } = [];
        public IReadOnlyList<SqlCommandSpec> LastTransactionCommands { get; private set; } = [];
        public string? LastTablePageConnectionString { get; private set; }
        public string? LastTablePageTableName { get; private set; }
        public IReadOnlyList<string> LastTablePageOrderByColumns { get; private set; } = [];
        public int LastTablePagePageSize { get; private set; }
        public int LastTablePagePageIndex { get; private set; }
        public QueryResult QueryResult { get; init; } = new();
        public TablePageResult PageResult { get; init; } = new();
        public TransactionResult? ConfiguredTransactionResult { get; init; }

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("Recording");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(QueryResult);

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null)
        {
            LastTablePageConnectionString = connectionString;
            LastTablePageTableName = tableName;
            LastTablePageOrderByColumns = orderByColumns.ToList();
            LastTablePagePageSize = pageSize;
            LastTablePagePageIndex = pageIndex;
            var pageResult = PageResult.Result.ColumnInfos.Count > 0 || PageResult.Result.ObjectRows.Count > 0
                ? PageResult
                : new TablePageResult
                {
                    Result = QueryResult,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                    HasNextPage = false
                };
            return Task.FromResult(pageResult);
        }
        // TableDataTabViewModel always loads through a TableQuerySpec now; route it through the
        // shared LIMIT/OFFSET renderer so ExecuteQueryAsync's configured QueryResult still applies.
        public TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
            TableQuerySql.BuildLimitOffsetPage(Capabilities, tableName, spec);

        public TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
            TableQuerySql.BuildCountQuery(Capabilities, tableName, spec);

        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(1);

        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements)
        {
            LastTransactionConnectionString = connectionString;
            LastTransactionStatements = statements.ToList();
            return Task.FromResult(ConfiguredTransactionResult ?? TransactionResult.Success(statements.Count));
        }

        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands)
        {
            LastTransactionConnectionString = connectionString;
            LastTransactionCommands = commands.ToList();
            return Task.FromResult(ConfiguredTransactionResult ?? TransactionResult.Success(commands.Count));
        }

        public string FormatLimitQuery(string query, int limit) => $"{query} LIMIT {limit}";
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => ["text"];
    }
}
