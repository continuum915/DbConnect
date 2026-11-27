using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Ticket 05: providers with <see cref="DatabaseProviderCapabilities.SupportsTableQuerySpec"/> report an
/// exact total honoring the active filters, the status bar shows "page X of Y", and the opt-out skips
/// the COUNT(*) query and shows the total as unknown.
/// </summary>
public sealed class TableDataTabExactTotalTests
{
    [Fact]
    public async Task Refresh_FetchesExactTotal_AndPageSummaryShowsPageOfY()
    {
        var session = new SpecSession(totalRows: 25);
        var tab = CreateTab(session);
        tab.PageSize = 10;

        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(25, tab.TotalRowCount);
        Assert.Equal("Page 1 of 3 · 25 row(s) total · Page size 10", tab.PageSummary);
        Assert.Contains("SELECT COUNT(*)", session.LastCountSql);
    }

    [Fact]
    public async Task ExactTotal_HonorsActiveFilters()
    {
        var session = new SpecSession(totalRows: 1);
        var tab = CreateTab(session);
        tab.AddFilterRow("Name", TableFilterOperator.Equals, "A");

        await tab.ApplyFiltersAsync();

        Assert.Equal(1, tab.TotalRowCount);
        Assert.NotNull(session.LastCountSql);
        Assert.Contains("WHERE", session.LastCountSql);
        Assert.Contains("\"Name\"", session.LastCountSql);
        Assert.Equal(["A"], session.LastCountParameters);
    }

    [Fact]
    public async Task SkipExactCount_OptOut_LeavesTotalUnknown_AndSkipsTheCountQuery()
    {
        var session = new SpecSession(totalRows: 25);
        var tab = CreateTab(session);
        tab.SkipExactCount = true;

        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Null(tab.TotalRowCount);
        Assert.Null(session.LastCountSql);
        Assert.DoesNotContain("of", tab.PageSummary);
    }

    [Fact]
    public async Task TurningSkipExactCountOn_ClearsTotalWithoutQuerying()
    {
        var session = new SpecSession(totalRows: 25);
        var tab = CreateTab(session);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        Assert.Equal(25, tab.TotalRowCount);
        session.CountCalls = 0;

        tab.SkipExactCount = true;

        Assert.True(tab.SkipExactCount);
        Assert.Null(tab.TotalRowCount);
        Assert.Equal(0, session.CountCalls);
    }

    [Fact]
    public async Task TurningSkipExactCountOff_RefetchesTheExactTotal()
    {
        var session = new SpecSession(totalRows: 25);
        var tab = CreateTab(session);
        tab.SkipExactCount = true;
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        Assert.Null(tab.TotalRowCount);

        tab.SkipExactCount = false;

        Assert.False(tab.SkipExactCount);
        Assert.Equal(25, tab.TotalRowCount);
    }

    [Fact]
    public async Task TurningSkipExactCountOff_WithDirtyRows_RevertsAndKeepsTheCheckboxInSync()
    {
        // Regression coverage: the checkbox binds IsChecked two-way straight to SkipExactCount, so a
        // blocked toggle must revert the property itself, or the checkbox would show a state the
        // property disagrees with.
        var session = new SpecSession(totalRows: 25);
        var tab = CreateTab(session);
        tab.SkipExactCount = true;
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(tab.Rows);
        row.Values["Name"] = "Changed";
        tab.MarkRowModified(row);
        Assert.True(tab.HasDirtyRows);

        tab.SkipExactCount = false;

        Assert.True(tab.SkipExactCount);
        Assert.Null(tab.TotalRowCount);
    }

    private static TableDataTabViewModel CreateTab(SpecSession session)
    {
        var provider = new SpecProvider(session);
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        table.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        table.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });
        var connection = new DatabaseSession(new ConnectionInfo { Name = "Local", Provider = provider.ProviderName, ConnectionString = "x" }, provider);
        return new TableDataTabViewModel("Customers", connection, table);
    }

    private sealed class SpecSession(long totalRows) : IDbSession
    {
        public IDatabaseProvider Provider { get; set; } = null!;
        public string? LastCountSql { get; private set; }
        public IReadOnlyList<object?>? LastCountParameters { get; private set; }
        public int CountCalls { get; set; }
        public bool IsInTransaction => false;

        public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;

        public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null)
        {
            if (query.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal))
            {
                CountCalls++;
                LastCountSql = query;
                LastCountParameters = options?.Parameters;
                return Task.FromResult(new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "count", Ordinal = 0 }],
                    ObjectRows = [new object?[] { totalRows }]
                });
            }

            return Task.FromResult(new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1L, "A" }]
            });
        }

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string query,
            QueryExecutionOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new QueryResult();
        }

        public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) =>
            Task.FromResult(TransactionResult.Success(commands.Count));

        public Task<TablePageResult> LoadTablePageAsync(
            string tableName,
            IReadOnlyList<string> orderByColumns,
            int pageSize,
            int pageIndex,
            QueryExecutionOptions? options = null) =>
            throw new NotSupportedException("This fixture only exercises the TableQuerySpec path.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SpecProvider(SpecSession session) : IDatabaseProvider
    {
        public string ProviderName => "Spec";
        public string ProviderIcon => "S";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT") { SupportsTableQuerySpec = true };
        public string ExplainPrefix => "EXPLAIN";

        public Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
        {
            session.Provider = this;
            return Task.FromResult<IDbSession>(session);
        }

        public TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
            TableQuerySql.BuildLimitOffsetPage(Capabilities, tableName, spec);

        public TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
            TableQuerySql.BuildCountQuery(Capabilities, tableName, spec);

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("s");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }

        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult());

        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(0));
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(0));
        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => [];
    }
}
