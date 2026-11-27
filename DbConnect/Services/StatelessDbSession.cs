using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Default session for providers without a connection-holding session: every
/// call goes through the provider's connection-string methods, which open a
/// fresh connection. It keeps no session state and cannot hold a transaction.
/// </summary>
public sealed class StatelessDbSession : IDbSession
{
    private readonly string _connectionString;

    public StatelessDbSession(IDatabaseProvider provider, ConnectionInfo connection)
    {
        Provider = provider;
        _connectionString = connection.ConnectionString;
    }

    public IDatabaseProvider Provider { get; }

    public bool IsInTransaction => false;

    public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => throw TransactionsNotSupported();

    public Task CommitAsync(CancellationToken cancellationToken = default) => throw TransactionsNotSupported();

    public Task RollbackAsync(CancellationToken cancellationToken = default) => throw TransactionsNotSupported();

    public Task CancelAsync() => Task.CompletedTask;

    public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) =>
        Provider.ExecuteQueryAsync(_connectionString, query, options);

    public Task<TablePageResult> LoadTablePageAsync(
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null) =>
        Provider.LoadTablePageAsync(_connectionString, tableName, orderByColumns, pageSize, pageIndex, options);

    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) =>
        Provider.ExecuteTransactionAsync(_connectionString, commands);

    public IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
        string query,
        QueryExecutionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Provider.ExecuteQueryMultipleAsync(_connectionString, query, options, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private NotSupportedException TransactionsNotSupported() =>
        new($"{Provider.ProviderName} does not support session transactions yet.");
}
