using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Lifetime rules for the session a query tab owns: one open at a time, never leaked, cancel goes through it.</summary>
public sealed class QueryTabSessionLifetimeTests : MainWindowTestBase
{
    [Fact]
    public async Task ConcurrentFirstUse_OpensOneSessionAndSharesIt()
    {
        var provider = new GatedProvider();
        var tab = CreateTab(provider, out _);

        var first = tab.GetOrOpenDbSessionAsync();
        var second = tab.GetOrOpenDbSessionAsync();
        provider.CompleteOpens();

        var sessions = await Task.WhenAll(first, second);

        Assert.Same(sessions[0], sessions[1]);
        Assert.Equal(1, provider.OpenCount);
    }

    [Fact]
    public async Task ChangingConnectionWhileOpening_DisposesTheStaleSessionAndKeepsNone()
    {
        var provider = new GatedProvider();
        var tab = CreateTab(provider, out var connections);

        var opening = tab.GetOrOpenDbSessionAsync();
        tab.SelectedSession = connections[1];
        provider.CompleteOpens();

        await Assert.ThrowsAsync<InvalidOperationException>(() => opening);

        Assert.False(tab.HasDbSession);
        Assert.All(provider.Sessions, session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task ChangingConnectionAwayAndBackWhileOpening_StillDiscardsTheStaleSession()
    {
        var provider = new GatedProvider();
        var tab = CreateTab(provider, out var connections);

        var opening = tab.GetOrOpenDbSessionAsync();
        tab.SelectedSession = connections[1];
        tab.SelectedSession = connections[0];
        provider.CompleteOpens();

        await Assert.ThrowsAsync<InvalidOperationException>(() => opening);

        Assert.False(tab.HasDbSession);
    }

    [Fact]
    public async Task CancelExecution_CancelsThroughTheTabsSession()
    {
        var provider = new GatedProvider();
        var tab = CreateTab(provider, out _);
        provider.CompleteOpens();
        await tab.GetOrOpenDbSessionAsync();

        tab.CancelExecutionCommand.Execute(null);

        Assert.Equal(1, Assert.Single(provider.Sessions).CancelCount);
    }

    [Fact]
    public async Task RunningOnAClosedSession_ReportsCancelledInsteadOfThrowing()
    {
        var provider = new DatabaseService().GetProvider("SQLite");
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rain-closed-{Guid.NewGuid():N}.sqlite");
        try
        {
            var session = await provider.OpenSessionAsync(new ConnectionInfo { ConnectionString = $"Data Source={path}" });
            await session.DisposeAsync();

            var result = await session.ExecuteQueryAsync("SELECT 1;");

            Assert.True(result.Error?.IsCanceled);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public async Task ManualCommit_WhenTheSessionCannotBeginATransaction_ReportsItAndRunsNothing()
    {
        var provider = new GatedProvider { BeginFails = true };
        var (vm, tab) = CreateViewModelWithTab(provider);
        provider.CompleteOpens();
        tab.IsManualCommit = true;
        tab.QueryText = "UPDATE items SET n = 1;";

        await vm.ExecuteQueryCommand.ExecuteAsync(null);

        Assert.True(tab.CurrentResult?.HasError);
        Assert.Contains("transaction", tab.CurrentResult?.Error?.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot start a transaction", tab.CurrentResult?.Error?.Detail);
        Assert.Empty(provider.ExecutedQueries);
        Assert.False(tab.IsInTransaction);
    }

    [Fact]
    public async Task Commit_WhenTheServerRefuses_ReportsItAndKeepsTheTransactionOpen()
    {
        var provider = new GatedProvider();
        var (vm, tab) = CreateViewModelWithTab(provider);
        provider.CompleteOpens();
        tab.IsManualCommit = true;
        tab.QueryText = "UPDATE items SET n = 1;";
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        Assert.True(tab.IsInTransaction);
        provider.CommitFails = true;

        await tab.CommitCommand.ExecuteAsync(null);

        Assert.Equal("Commit failed.", tab.CurrentResult?.Error?.Summary);
        Assert.Contains("refused the commit", tab.CurrentResult?.Error?.Detail);
        Assert.True(tab.IsInTransaction);
    }

    private (MainWindowViewModel Vm, QueryTabViewModel Tab) CreateViewModelWithTab(GatedProvider provider)
    {
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "gated", Provider = provider.ProviderName, ConnectionString = "gated" },
            provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        vm.ActiveSessionOptions.Add(session);
        var tab = vm.NewQueryTabWithSession(session);
        vm.SelectedTab = tab;
        return (vm, tab);
    }

    private static QueryTabViewModel CreateTab(GatedProvider provider, out List<DatabaseSession> connections)
    {
        connections =
        [
            new DatabaseSession(new ConnectionInfo { Name = "one", Provider = provider.ProviderName, ConnectionString = "one" }, provider),
            new DatabaseSession(new ConnectionInfo { Name = "two", Provider = provider.ProviderName, ConnectionString = "two" }, provider)
        ];
        var options = new System.Collections.ObjectModel.ObservableCollection<DatabaseSession>(connections);
        return new QueryTabViewModel("Query 1", options) { SelectedSession = connections[0] };
    }

    private sealed class RecordingSession : IDbSession
    {
        private readonly GatedProvider _owner;
        public RecordingSession(GatedProvider provider) { _owner = provider; Provider = provider; }
        public IDatabaseProvider Provider { get; }
        public bool Disposed { get; private set; }
        public int CancelCount { get; private set; }
        public bool IsInTransaction { get; private set; }

        public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            if (_owner.BeginFails)
                throw new NotSupportedException("This connection cannot start a transaction.");
            IsInTransaction = true;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_owner.CommitFails)
                throw new InvalidOperationException("The server refused the commit.");
            IsInTransaction = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            IsInTransaction = false;
            return Task.CompletedTask;
        }

        public Task CancelAsync() { CancelCount++; return Task.CompletedTask; }
        public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());
        public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult());
        public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(0));

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string query,
            QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            _owner.ExecutedQueries.Add(query);
            yield return new QueryResult { ExecutedSql = query };
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>A provider whose session opens only complete when the test says so.</summary>
    private sealed class GatedProvider : IDatabaseProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _openCount;

        public List<RecordingSession> Sessions { get; } = [];
        public int OpenCount => _openCount;
        public bool BeginFails { get; set; }
        public bool CommitFails { get; set; }
        public List<string> ExecutedQueries { get; } = [];

        public void CompleteOpens() => _gate.TrySetResult();

        public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            await _gate.Task;
            var session = new RecordingSession(this);
            lock (Sessions)
                Sessions.Add(session);
            return session;
        }

        public string ProviderName => "Gated";
        public string ProviderIcon => "G";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT");
        public string ExplainPrefix => "EXPLAIN";

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("gated");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
