using System.Linq;
using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>A table-data tab owns one session: reads and commits run on it, and it is never shared.</summary>
public sealed class TableDataTabSessionTests : MainWindowTestBase
{
    [Fact]
    public void NewTab_HasNoSessionUntilItFirstTouchesTheDatabase()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);

        Assert.False(tab.HasDbSession);
        Assert.Empty(provider.Sessions);
    }

    [Fact]
    public async Task Refresh_LoadsThePageOnTheTabsSession()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);

        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        var session = Assert.Single(provider.Sessions);
        Assert.Equal(2, session.PageLoads);
        Assert.Equal(0, provider.ConnectionStringCalls);
        Assert.Single(tab.Rows);
    }

    [Fact]
    public async Task Commit_RunsOnTheSameSessionAsTheReads()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(tab.Rows);
        row.Values["Name"] = "New";
        tab.MarkRowModified(row);

        await tab.ExecuteCommitAsync();

        var session = Assert.Single(provider.Sessions);
        Assert.Single(session.Transactions);
        Assert.Equal(0, provider.ConnectionStringCalls);
    }

    [Fact]
    public async Task Commit_ReselectsTheRow_WhenItsAllColumnIdentityIncludesBinary()
    {
        // A keyless table identified by all its columns, one of them binary. Every reload hands
        // back fresh byte arrays, so the selection must be matched by content, not reference.
        var provider = new SessionProvider
        {
            Page = () => new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Data", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1L, new byte[] { 1 } }, new object?[] { 2L, new byte[] { 2 } }]
            }
        };
        var table = new SchemaNode { Name = "Blobs", RawName = "Blobs", Type = SchemaNodeType.Table };
        table.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column });
        table.Children.Add(new SchemaNode { Name = "Data", RawName = "Data", Type = SchemaNodeType.Column });
        var tab = new TableDataTabViewModel("Blobs", CreateConnection(provider), table) { AllowAllColumnIdentityFallback = true };
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        tab.SelectedRow = tab.Rows[1];
        tab.DeleteRowCommand.Execute(tab.Rows[0]);

        await tab.ExecuteCommitAsync();

        Assert.Contains(tab.SelectedRow!, tab.Rows);
        Assert.Equal(2L, tab.SelectedRow!.Values["Id"]);
    }

    [Fact]
    public async Task TwoTableTabsOnTheSameConnection_DoNotShareASession()
    {
        var provider = new SessionProvider();
        var connection = CreateConnection(provider);
        var first = new TableDataTabViewModel("Customers", connection, null);
        var second = new TableDataTabViewModel("Customers", connection, null);

        await first.RefreshInternalAsync(bypassDirtyCheck: true);
        await second.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(2, provider.Sessions.Count);
        Assert.NotSame(provider.Sessions[0], provider.Sessions[1]);
    }

    [Fact]
    public async Task TableTabAndQueryTabOnTheSameConnection_DoNotShareASession()
    {
        var provider = new SessionProvider();
        var connection = CreateConnection(provider);
        var table = new TableDataTabViewModel("Customers", connection, null);
        var query = new QueryTabViewModel("Query 1", new System.Collections.ObjectModel.ObservableCollection<DatabaseSession> { connection })
        {
            SelectedSession = connection
        };

        await table.RefreshInternalAsync(bypassDirtyCheck: true);
        await query.GetOrOpenDbSessionAsync();

        Assert.Equal(2, provider.Sessions.Count);
        Assert.NotSame(provider.Sessions[0], provider.Sessions[1]);
    }

    [Fact]
    public async Task ReleasingTheSession_DisposesIt_AndLaterReadsOpenNoneAgain()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        await tab.ReleaseDbSessionAsync();
        await tab.ReleaseDbSessionAsync();

        Assert.False(tab.HasDbSession);
        Assert.True(Assert.Single(provider.Sessions).Disposed);

        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Single(provider.Sessions);
        Assert.False(tab.HasDbSession);
    }

    [Fact]
    public async Task ReleasingWhileASessionIsOpening_DisposesTheStaleSessionAndAdoptsNone()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);

        var refresh = tab.RefreshInternalAsync(bypassDirtyCheck: true);
        await tab.ReleaseDbSessionAsync();
        await refresh;

        Assert.False(tab.HasDbSession);
        Assert.All(provider.Sessions, session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task ClosingTheTab_DisposesItsSession()
    {
        var provider = new SessionProvider();
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var tab = CreateTab(provider);
        vm.Tabs.Add(tab);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.True(Assert.Single(provider.Sessions).Disposed);
        Assert.False(tab.HasDbSession);
    }

    [Fact]
    public async Task DisconnectingTheConnection_DisposesTheSessionsOfItsTableTabsOnly()
    {
        var provider = new SessionProvider();
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var connection = CreateConnection(provider);
        var otherConnection = CreateConnection(provider);
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        vm.SchemaNodes.Add(root);
        vm.ActiveConnections[root] = connection.Connection;
        vm.ActiveSessions[root] = connection;
        vm.ActiveSessionOptions.Add(connection);
        var owned = new TableDataTabViewModel("Customers", connection, null);
        var unrelated = new TableDataTabViewModel("Orders", otherConnection, null);
        vm.Tabs.Add(owned);
        vm.Tabs.Add(unrelated);
        await owned.RefreshInternalAsync(bypassDirtyCheck: true);
        await unrelated.RefreshInternalAsync(bypassDirtyCheck: true);

        vm.Disconnect(root);

        Assert.False(owned.HasDbSession);
        Assert.True(unrelated.HasDbSession);
        Assert.Single(provider.Sessions, session => session.Disposed);
    }

    [Fact]
    public async Task OverlappingFirstUses_OpenOneSession()
    {
        var provider = new SessionProvider();
        var tab = CreateTab(provider);

        await Task.WhenAll(tab.RefreshInternalAsync(true), tab.RefreshInternalAsync(true));

        Assert.Single(provider.Sessions);
    }

    [Fact]
    public async Task OverlappingRefreshes_WaitForTheCurrentPageToFinish()
    {
        var provider = new SessionProvider { PauseEnumLoad = true };
        var tab = CreateTab(provider);
        var first = tab.RefreshInternalAsync(true);
        Task? second = null;
        try
        {
            await provider.EnumLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = tab.RefreshInternalAsync(true);
            Assert.Equal(1, Assert.Single(provider.Sessions).PageLoads);
            provider.EnumLoadRelease.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["Id", "Name"], tab.Columns);
            Assert.Single(tab.Rows);
        }
        finally
        {
            provider.EnumLoadRelease.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second != null)
                await second.WaitAsync(TimeSpan.FromSeconds(10));
            await tab.ReleaseDbSessionAsync();
        }
    }

    [Fact]
    public async Task SqliteSession_TransactionRollsBackTogetherOnFailureAndMismatch_AndEnlistsInAnOpenTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-tabtx-{Guid.NewGuid():N}.sqlite");
        try
        {
            var provider = new DatabaseService().GetProvider("SQLite");
            await using var session = await provider.OpenSessionAsync(new ConnectionInfo { ConnectionString = $"Data Source={path}" });
            await session.ExecuteQueryAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, n TEXT); INSERT INTO t VALUES (1, 'a');");

            var failed = await session.ExecuteTransactionAsync(
            [
                new SqlCommandSpec("UPDATE t SET n = @p0 WHERE id = @p1;", new Dictionary<string, object?> { ["@p0"] = "b", ["@p1"] = 1L }),
                new SqlCommandSpec("UPDATE nope SET n = 'x';", new Dictionary<string, object?>())
            ]);
            var mismatch = await session.ExecuteTransactionAsync(
            [
                new SqlCommandSpec("UPDATE t SET n = @p0 WHERE id = @p1;", new Dictionary<string, object?> { ["@p0"] = "c", ["@p1"] = 99L }, expectedRecordsAffected: 1)
            ]);
            var unchanged = await session.ExecuteQueryAsync("SELECT n FROM t;");

            Assert.False(failed.Succeeded);
            Assert.True(mismatch.IsConcurrencyConflict);
            Assert.Equal("a", unchanged.ObjectRows[0][unchanged.IndexOfColumn("n")]);
            Assert.False(session.IsInTransaction);

            await session.BeginTransactionAsync();
            var enlisted = await session.ExecuteTransactionAsync(
            [
                new SqlCommandSpec("UPDATE t SET n = @p0 WHERE id = @p1;", new Dictionary<string, object?> { ["@p0"] = "d", ["@p1"] = 1L })
            ]);
            Assert.True(enlisted.Succeeded);
            Assert.True(session.IsInTransaction);
            await session.RollbackAsync();
            var reloaded = await session.ExecuteQueryAsync("SELECT n FROM t;");
            Assert.Equal("a", reloaded.ObjectRows[0][reloaded.IndexOfColumn("n")]);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static DatabaseSession CreateConnection(SessionProvider provider) =>
        new(new ConnectionInfo { Name = "Local", Provider = provider.ProviderName, ConnectionString = "x" }, provider);

    private static TableDataTabViewModel CreateTab(SessionProvider provider)
    {
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        table.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        table.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });
        return new TableDataTabViewModel("Customers", CreateConnection(provider), table);
    }

    private sealed class PageSession(IDatabaseProvider provider, Func<QueryResult>? page) : IDbSession
    {
        public IDatabaseProvider Provider { get; } = provider;
        public bool Disposed { get; private set; }
        public int PageLoads { get; private set; }
        public List<IReadOnlyList<SqlCommandSpec>> Transactions { get; } = [];
        public bool IsInTransaction => false;

        public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string query,
            QueryExecutionOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new QueryResult();
        }

        public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null)
        {
            PageLoads++;
            return Task.FromResult(new TablePageResult
            {
                Result = page?.Invoke() ?? new QueryResult
                {
                    ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                    ObjectRows = [new object?[] { 1L, "Old" }]
                }
            });
        }

        // TableDataTabViewModel always loads through a TableQuerySpec now; route it back through
        // the canned orderByColumns page above rather than rendering real SQL.
        public Task<TablePageResult> LoadTablePageAsync(string tableName, TableQuerySpec spec, QueryExecutionOptions? options = null) =>
            LoadTablePageAsync(tableName, spec.Sort.Select(k => k.Column).ToList(), spec.PageSize, spec.PageIndex, options);

        public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands)
        {
            Transactions.Add(commands);
            return Task.FromResult(TransactionResult.Success(commands.Count));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SessionProvider : IDatabaseProvider
    {
        public List<PageSession> Sessions { get; } = [];
        public int ConnectionStringCalls { get; private set; }
        public bool PauseEnumLoad { get; init; }
        public TaskCompletionSource EnumLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnumLoadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default)
        {
            if (PauseEnumLoad)
            {
                EnumLoadStarted.TrySetResult();
                await EnumLoadRelease.Task;
            }
            return [];
        }

        /// <summary>Builds each page load's result, replacing the default one-row page.</summary>
        public Func<QueryResult>? Page { get; init; }

        public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var session = new PageSession(this, Page);
            lock (Sessions)
                Sessions.Add(session);
            return session;
        }

        public string ProviderName => "Sessions";
        public string ProviderIcon => "S";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT");
        public string ExplainPrefix => "EXPLAIN";

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("s");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) { ConnectionStringCalls++; return Task.FromResult(new QueryResult()); }

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) { ConnectionStringCalls++; return Task.FromResult(new TablePageResult()); }
        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) { ConnectionStringCalls++; return Task.FromResult(0); }
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) { ConnectionStringCalls++; return Task.FromResult(TransactionResult.Success(0)); }
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) { ConnectionStringCalls++; return Task.FromResult(TransactionResult.Success(0)); }
        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => [];
    }
}
