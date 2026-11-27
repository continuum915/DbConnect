using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Session that owns one open <see cref="DbConnection"/>. Commands are
/// serialized (a connection is not thread-safe), every command enlists in the
/// session transaction, and <see cref="CancelAsync"/> cancels the run in flight
/// without closing the session.
/// </summary>
public sealed class ConnectionDbSession : IDbSession, IQueryExportSession
{
    /// <summary>Runs one statement on the session connection, inside the transaction when one is open.</summary>
    public delegate Task<QueryResult> Executor(
        DbConnection connection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options);

    /// <summary>Runs one statement and reads every result set it produces natively.</summary>
    public delegate Task<List<QueryResult>> MultiExecutor(
        DbConnection connection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options);

    private readonly DbConnection _connection;
    private readonly Executor _executor;
    private readonly MultiExecutor _multiExecutor;
    private readonly bool _namedParameters;
    private readonly Action<DbParameter>? _configureParameter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cancelLock = new();
    private volatile DbTransaction? _transaction;
    private CancellationTokenSource _cancel = new();
    private int _disposed;
    private SessionQueryExport? _queryExport;
    private volatile ResultContinuation? _pending;
    private Task? _pendingRun;
    private Func<DbConnection, DbCommand, CancellationToken, IDisposable>? _queryExportCancellation;
    private Action<DbDataReader>? _queryExportCompletion;

    /// <param name="namedParameters">False for drivers that bind parameters by position and reject names.</param>
    /// <param name="configureParameter">Adjusts each bound DML parameter after its value is set, for drivers that need a type hint.</param>
    private ConnectionDbSession(IDatabaseProvider provider, DbConnection openConnection, Executor executor, MultiExecutor multiExecutor, bool namedParameters, Action<DbParameter>? configureParameter)
    {
        Provider = provider;
        _connection = openConnection;
        _executor = executor;
        _multiExecutor = multiExecutor;
        _namedParameters = namedParameters;
        _configureParameter = configureParameter;
    }

    /// <summary>
    /// Opens <paramref name="connection"/> and wraps it in a session; the connection is disposed if opening fails.
    /// The executors receive the connection as its own type, so providers need no casts.
    /// </summary>
    public static async Task<IDbSession> OpenAsync<TConnection>(
        IDatabaseProvider provider,
        TConnection connection,
        Func<TConnection, DbTransaction?, string, QueryExecutionOptions?, Task<QueryResult>> executor,
        Func<TConnection, DbTransaction?, string, QueryExecutionOptions?, Task<List<QueryResult>>> multiExecutor,
        CancellationToken cancellationToken,
        bool namedParameters = true,
        Action<DbParameter>? configureParameter = null,
        Func<TConnection, DbCommand, CancellationToken, IDisposable>? queryExportCancellation = null,
        Action<DbDataReader>? queryExportCompletion = null)
        where TConnection : DbConnection
    {
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            try { await connection.DisposeAsync(); }
            catch (Exception cleanup) { ex.Data["ConnectionDisposalError"] = cleanup.Message; }
            throw;
        }

        var session = new ConnectionDbSession(
            provider,
            connection,
            (conn, transaction, query, options) => executor((TConnection)conn, transaction, query, options),
            (conn, transaction, query, options) => multiExecutor((TConnection)conn, transaction, query, options),
            namedParameters,
            configureParameter);
        session._queryExportCompletion = queryExportCompletion;
        if (queryExportCancellation != null)
        {
            session._queryExportCancellation = (conn, command, token) => queryExportCancellation((TConnection)conn, command, token);
            session._queryExport = new SessionQueryExport(session.ReadExportAsync, () => session.IsDisposed, provider.Capabilities.BatchDialect);
        }
        return session;
    }

    public IDatabaseProvider Provider { get; }

    public bool IsInTransaction => _transaction != null;

    /// <summary>The server or network closed the connection; the session cannot run anything again.</summary>
    public bool IsConnectionLost => !IsDisposed && _connection.State is System.Data.ConnectionState.Broken or System.Data.ConnectionState.Closed;

    /// <summary>Set for a read-only connection: grid edits are refused with this reason instead of running.</summary>
    public string? WriteRefusal { get; set; }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_transaction != null)
                throw new InvalidOperationException("A transaction is already open on this session.");
            _transaction = await _connection.BeginTransactionAsync(cancellationToken);
            _queryExport?.Invalidate();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        EndTransactionAsync(commit: true, cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        EndTransactionAsync(commit: false, cancellationToken);

    public Task CancelAsync()
    {
        CancelCurrentRuns(replaceSource: true);
        return Task.CompletedTask;
    }

    public async Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null)
    {
        var results = await RunGatedAsync(query, options, async (effective, linked) =>
        {
            async Task<IReadOnlyList<QueryResult>> Single() => [await _executor(_connection, _transaction, query, effective)];
            return await ParkOrCompleteAsync(Single(), query, effective, linked);
        });
        return results[0];
    }

    public async Task<TablePageResult> LoadTablePageAsync(
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null)
    {
        var query = Provider.BuildTablePageQuery(tableName, orderByColumns, pageSize, pageIndex);
        var result = await ExecuteQueryAsync(query, options);
        return TablePageResult.FromQuery(result, pageIndex, pageSize);
    }

    public async Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands)
    {
        if (WriteRefusal != null)
            return new TransactionResult { Outcome = TransactionOutcome.NoChanges, ErrorMessage = WriteRefusal };
        await EnterAsync(default);
        try
        {
            ThrowIfDisposed();

            _queryExport?.Invalidate();
            return await TransactionExecution.ExecuteAsync(
                _connection, _transaction, commands, _namedParameters, _configureParameter);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
        string query,
        QueryExecutionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effective = options ?? new QueryExecutionOptions();
        if (effective.CancellationToken == default && cancellationToken != default)
            effective = effective with { CancellationToken = cancellationToken };

        var results = await RunMultiAsync(query, effective);
        for (var i = 0; i < results.Count; i++)
        {
            results[i].ResultIndex = i;
            results[i].TabLabel = results.Count > 1 ? $"Result {i + 1}" : "Results";
            yield return results[i];
        }
    }

    /// <summary>Same gating/transaction-typing as <see cref="ExecuteQueryAsync"/>, routed through the multi-result executor.</summary>
    private async Task<List<QueryResult>> RunMultiAsync(string query, QueryExecutionOptions options) =>
        [.. await RunGatedAsync(query, options, async (effective, linked) =>
        {
            async Task<IReadOnlyList<QueryResult>> Multi() => await _multiExecutor(_connection, _transaction, query, effective);
            return await ParkOrCompleteAsync(Multi(), query, effective, linked);
        })];

    /// <summary>
    /// Runs one statement under the gate. A typed COMMIT/ROLLBACK ends the session transaction through the
    /// driver, so the driver never believes a transaction is open that the server has ended. When the run
    /// parks a capped reader, the gate stays held (and the linked token alive) until the result is closed.
    /// </summary>
    private async Task<IReadOnlyList<QueryResult>> RunGatedAsync(string query, QueryExecutionOptions? options,
        Func<QueryExecutionOptions, CancellationTokenSource, Task<(IReadOnlyList<QueryResult> Results, bool Parked)>> run)
    {
        if (IsDisposed)
            return [CanceledResult(query)];

        var linked = CancellationTokenSource.CreateLinkedTokenSource(options?.CancellationToken ?? default, CurrentCancelToken());
        var effective = SessionQueryExport.Snapshot(options) with
        {
            CancellationToken = linked.Token,
            Continuation = options?.Continuation
        };
        try
        {
            await EnterAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            linked.Dispose();
            return [CanceledResult(query)];
        }

        var parked = false;
        try
        {
            // The session may have been closed while this run waited its turn.
            if (IsDisposed)
                return [CanceledResult(query)];

            var end = _transaction != null ? TransactionControl.Classify(query) : TransactionEnd.None;
            if (end != TransactionEnd.None)
                return [await RunTransactionEndAsync(query, commit: end == TransactionEnd.Commit, effective.CancellationToken)];

            effective.Continuation?.Arm();
            var outcome = await run(effective, linked);
            parked = outcome.Parked;
            return outcome.Results;
        }
        finally
        {
            if (!parked)
            {
                linked.Dispose();
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Waits for the run, or for its capped reader to park. Parked: the first page goes back now, and the
    /// run finishes in the background when the result is closed, then releases the gate.
    /// </summary>
    private async Task<(IReadOnlyList<QueryResult> Results, bool Parked)> ParkOrCompleteAsync(
        Task<IReadOnlyList<QueryResult>> run, string query, QueryExecutionOptions effective, CancellationTokenSource linked)
    {
        if (effective.Continuation is { } continuation)
        {
            if (await Task.WhenAny(run, continuation.Parked) != run)
            {
                var firstPage = continuation.ResultsSoFar;
                foreach (var result in firstPage) result.ExecutedSql ??= query;
                _pending = continuation;
                _pendingRun = FinishParkedAsync(run, query, effective, linked);
                return (firstPage, true);
            }
        }
        var results = await run;
        _queryExport?.Record(query, effective, results);
        return (results, false);
    }

    private async Task FinishParkedAsync(Task<IReadOnlyList<QueryResult>> run, string query, QueryExecutionOptions effective,
        CancellationTokenSource linked)
    {
        try
        {
            var results = await run;
            _queryExport?.Record(query, effective, results);
        }
        catch { /* the parked result reports its own read errors */ }
        finally
        {
            _pending = null;
            linked.Dispose();
            _gate.Release();
        }
    }

    /// <summary>The result whose reader is parked on this session, if any.</summary>
    public ResultContinuation? PendingResult => _pending;

    /// <summary>Closes a parked result and waits until its reader is disposed and the connection is free.</summary>
    public async Task ClosePendingResultAsync()
    {
        if (_pending is not { } pending) return;
        var run = _pendingRun;
        await pending.CloseAsync();
        if (run != null) await run;
    }

    /// <summary>Takes the gate, first closing any parked result that holds it.</summary>
    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await ClosePendingResultAsync();
            if (await _gate.WaitAsync(50, cancellationToken)) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Stop the run in flight so the gate frees up, then close under the gate.
        CancelCurrentRuns(replaceSource: false);
        await EnterAsync(default);
        try
        {
            TransactionBoundaryException? failure = null;
            try
            {
                if (_transaction != null)
                    await EndOpenTransactionAsync(commit: false, default);
            }
            catch (TransactionBoundaryException ex)
            {
                failure = ex;
            }
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex) when (failure != null)
            {
                failure.Result.BoundaryErrorMessage += $"; Connection disposal: {ex.Message}";
            }
            if (failure != null)
                throw failure;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndTransactionAsync(bool commit, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EndOpenTransactionAsync(commit, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Commits or rolls back the open transaction. The caller holds the gate.</summary>
    private async Task<TransactionResult> EndOpenTransactionAsync(bool commit, CancellationToken cancellationToken)
    {
        var transaction = _transaction ?? throw new InvalidOperationException("No transaction is open on this session.");
        _queryExport?.Invalidate();
        TransactionResult result;
        Exception? failure = null;
        try
        {
            if (commit)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);
            result = new TransactionResult
            {
                Succeeded = true,
                Outcome = commit ? TransactionOutcome.Committed : TransactionOutcome.RolledBack
            };
        }
        catch (Exception ex)
        {
            failure = ex;
            result = new TransactionResult
            {
                Outcome = TransactionOutcome.Unknown,
                ErrorMessage = ex.Message,
                BoundaryErrorMessage = $"{(commit ? "Commit" : "Rollback")}: {ex.Message}"
            };
        }
        _transaction = null;
        try { await transaction.DisposeAsync(); }
        catch (Exception ex)
        {
            result.BoundaryErrorMessage = $"{result.BoundaryErrorMessage} Transaction disposal: {ex.Message}".Trim();
            failure ??= ex;
        }
        if (failure != null)
            throw new TransactionBoundaryException(result, failure);
        return result;
    }

    /// <summary>Runs a typed COMMIT or ROLLBACK as a driver commit or rollback and reports it like any other statement.</summary>
    private async Task<QueryResult> RunTransactionEndAsync(string query, bool commit, CancellationToken cancellationToken)
    {
        var result = new QueryResult { ExecutedSql = query };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result.TransactionResult = await EndOpenTransactionAsync(commit, cancellationToken);
        }
        catch (TransactionBoundaryException ex)
        {
            result.TransactionResult = ex.Result;
            result.Error = QueryErrorInfo.Failure(Provider.ProviderName, ex.Message, ex.Message);
        }
        catch (OperationCanceledException)
        {
            result.Error = QueryCancellation.CancelledError(Provider.ProviderName);
        }
        catch (Exception ex)
        {
            result.Error = QueryErrorInfo.Failure(Provider.ProviderName, ex.Message, ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public QueryExportCapture? CaptureQueryExport(QueryResult result) => _queryExport?.Capture(result);

    private async IAsyncEnumerable<IReadOnlyList<object?>> ReadExportAsync(string sql, QueryExecutionOptions options,
        IReadOnlyList<ExportColumn> columns, Func<bool> isCurrent, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CurrentCancelToken());
        var token = linked.Token;
        await EnterAsync(token);
        try
        {
            ThrowIfDisposed();
            if (!isCurrent()) throw new InvalidOperationException("Original export session context changed.");
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = _transaction;
            if (options.TimeoutSeconds > 0) command.CommandTimeout = options.TimeoutSeconds;
            for (var i = 0; i < (options.Parameters?.Count ?? 0); i++)
            {
                var parameter = command.CreateParameter();
                if (_namedParameters) parameter.ParameterName = $"@p{i}";
                parameter.Value = options.Parameters![i] ?? DBNull.Value;
                _configureParameter?.Invoke(parameter);
                command.Parameters.Add(parameter);
            }
            using var registration = _queryExportCancellation?.Invoke(_connection, command, token);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (reader.FieldCount != columns.Count) throw new InvalidOperationException("Export columns changed.");
            for (var i = 0; i < columns.Count; i++)
                if (reader.GetName(i) != columns[i].Name) throw new InvalidOperationException("Export columns changed.");
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var row = new object?[columns.Count];
                for (var i = 0; i < row.Length; i++) row[i] = DbReaderResults.ReadValue(reader, i);
                yield return row;
            }
            _queryExportCompletion?.Invoke(reader);
            if (await reader.NextResultAsync(token)) throw new InvalidOperationException("Ambiguous export result.");
            token.ThrowIfCancellationRequested();
        }
        finally { _gate.Release(); }
    }

    private CancellationToken CurrentCancelToken()
    {
        lock (_cancelLock)
            return _cancel.Token;
    }

    // Sources are never disposed: a run may still be linked to one, and a
    // source without timers holds nothing that needs releasing.
    private void CancelCurrentRuns(bool replaceSource)
    {
        CancellationTokenSource previous;
        lock (_cancelLock)
        {
            previous = _cancel;
            if (replaceSource)
                _cancel = new CancellationTokenSource();
        }

        previous.Cancel();
    }

    private QueryResult CanceledResult(string query) => new()
    {
        ExecutedSql = query,
        Error = QueryCancellation.CancelledError(Provider.ProviderName)
    };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(IsDisposed, this);
}
