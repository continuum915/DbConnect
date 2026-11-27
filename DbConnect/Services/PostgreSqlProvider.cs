using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>RAIN-102 — PostgreSQL provider via Npgsql.</summary>
public class PostgreSqlProvider : DatabaseProviderRoles, IDatabaseProvider
{
    public PostgreSqlProvider()
    {
        Routine = new PostgreSqlRoutineProvider(this);
    }

    protected override IDatabaseSchemaProvider Schema { get; } = new PostgreSqlSchemaProvider();
    protected override IDatabaseRoutineProvider Routine { get; }
    protected override IDatabaseAdministrationProvider Administration { get; } = new PostgreSqlAdministrationProvider();

    public string ProviderName => "PostgreSQL";
    public string ProviderIcon => "🐘";
    public string ExplainPrefix => "EXPLAIN (BUFFERS, FORMAT TEXT)";
    public DatabaseProviderCapabilities Capabilities => ProviderCapabilityDeclarations.PostgreSql;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) =>
        ConnectionTester.RunAsync(async () =>
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = 5
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return $"PostgreSQL {connection.ServerVersion}";
        }, PostgreSqlConnectionFailures.Classify);

    public async Task<string> GetServerVersionAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return $"PostgreSQL {connection.ServerVersion}";
        }
        catch
        {
            return "PostgreSQL (version unknown)";
        }
    }

    public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        // A session owns its connection outright, so it never borrows one from the pool.
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false };
        return await ConnectionDbSession.OpenAsync(
            this,
            PostgreSqlConnections.Create(builder.ConnectionString),
            (conn, transaction, query, options) => ExecuteQueryCoreAsync(null, conn, transaction, query, options),
            (conn, transaction, query, options) => ExecuteQueryMultipleCoreAsync(null, conn, transaction, query, options),
            cancellationToken,
            configureParameter: BindStringsAsUnknown,
            queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
    }

    /// <summary>
    /// Sends a string parameter as PostgreSQL's <c>unknown</c> type, the way a quoted literal is, so
    /// the server types it from context. Npgsql otherwise sends it as <c>text</c>, and PostgreSQL has
    /// no implicit cast from text to integer, json or enum types, so a filter value ("amount > $1")
    /// or a JSON/enum cell edit ("SET payload = $1") fails with "operator does not exist" or
    /// "column is of type json but expression is of type text".
    /// </summary>
    private static void BindStringsAsUnknown(DbParameter parameter)
    {
        if (parameter is NpgsqlParameter { Value: string } npgsqlParameter)
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Unknown;
    }

    public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) =>
        ExecuteQueryCoreAsync(connectionString, null, null, query, options);

    public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
        string connectionString,
        string query,
        QueryExecutionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new QueryExecutionOptions();
        if (effectiveOptions.CancellationToken == default && cancellationToken != default)
            effectiveOptions = effectiveOptions with { CancellationToken = cancellationToken };

        var results = await ExecuteQueryMultipleCoreAsync(connectionString, null, null, query, effectiveOptions);
        for (var i = 0; i < results.Count; i++)
        {
            results[i].ResultIndex = i;
            results[i].TabLabel = results.Count > 1 ? $"Result {i + 1}" : "Results";
            yield return results[i];
        }
    }

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection. Drains every result set the reader produces.</summary>
    private async Task<List<QueryResult>> ExecuteQueryMultipleCoreAsync(
        string? connectionString,
        NpgsqlConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var stopwatch = Stopwatch.StartNew();
        var notices = new List<string>();
        try
        {
            await using var lease = await ConnectionLease<NpgsqlConnection>.AcquireAsync(
                sharedConnection, () => PostgreSqlConnections.Create(connectionString!), options?.CancellationToken ?? default);
            var connection = lease.Connection;
            using var collector = new QueryNoticeCollector(connection, notices);
            await using var cmd = new NpgsqlCommand(query, connection);
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                BindStringsAsUnknown(cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value));
            if (transaction != null)
                cmd.Transaction = (NpgsqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            var results = await PostgreSqlQueryResults.ReadAllAsync(cmd, options);
            QueryResultBatchMetadata.Apply(results, query, stopwatch.Elapsed, notices);
            return results;
        }
        catch (OperationCanceledException)
        {
            return [QueryResultBatchMetadata.Error(query, QueryCancellation.CancelledError(ProviderName), stopwatch.Elapsed, notices)];
        }
        catch (PostgresException ex)
        {
            return [QueryResultBatchMetadata.Error(query, new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.MessageText,
                Detail = ex.Detail,
                Code = ex.SqlState, Position = QueryErrorPositionExtractor.PostgreSql(ex.Position),
                IsTimeout = ex.SqlState == PostgresErrorCodes.QueryCanceled
            }, stopwatch.Elapsed, notices)];
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return [QueryResultBatchMetadata.Error(query, new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = "The query timed out.",
                Detail = ex.Message,
                IsTimeout = true
            }, stopwatch.Elapsed, notices)];
        }
        catch (Exception ex)
        {
            return [QueryResultBatchMetadata.Error(query, new QueryErrorInfo { Provider = ProviderName, Summary = ex.Message, Detail = ex.Message }, stopwatch.Elapsed, notices)];
        }
    }

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection.</summary>
    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string? connectionString,
        NpgsqlConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var result = new QueryResult
        {
            ExecutedSql = query
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var lease = await ConnectionLease<NpgsqlConnection>.AcquireAsync(
                sharedConnection, () => PostgreSqlConnections.Create(connectionString!), options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = new NpgsqlCommand(query, connection);
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                BindStringsAsUnknown(cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value));
            if (transaction != null)
                cmd.Transaction = (NpgsqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            await PostgreSqlQueryResults.ReadAsync(cmd, result, options);
        }
        // Npgsql reports a cancel as OperationCanceledException. SQLSTATE 57014 is deliberately
        // not treated as a cancel: it is also what a statement timeout raises, which is a real error.
        catch (OperationCanceledException)
        {
            result.Error = QueryCancellation.CancelledError(ProviderName);
        }
        catch (PostgresException ex)
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.MessageText,
                Detail = ex.Detail,
                Code = ex.SqlState,
                Position = QueryErrorPositionExtractor.PostgreSql(ex.Position),
                IsTimeout = ex.SqlState == PostgresErrorCodes.QueryCanceled
            };
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = "The query timed out.",
                Detail = ex.Message,
                IsTimeout = true
            };
        }
        catch (Exception ex)
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message
            };
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) =>
        PostgreSqlConnections.ExecuteNonQueryAsync(connectionString, sql);

    public async Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements)
    {
        var commands = statements
            .Select(statement => new SqlCommandSpec(statement, new Dictionary<string, object?>()))
            .ToList();
        return await ExecuteTransactionAsync(connectionString, commands);
    }

    public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) =>
        TransactionExecution.OpenAndExecuteAsync(() => new NpgsqlConnection(connectionString), commands, configureParameter: BindStringsAsUnknown);

    public string FormatLimitQuery(string query, int limit) =>
        $"{query.TrimEnd(';')} LIMIT {limit};";

    public string BuildTablePageQuery(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex)
    {
        var offset = Math.Max(pageIndex, 0) * pageSize;
        var quotedTable = QuoteQualifiedIdentifier(tableName);
        var orderClause = BuildOrderClause(orderByColumns);
        return $"SELECT * FROM {quotedTable}{orderClause} LIMIT {pageSize + 1} OFFSET {offset};";
    }

    public async Task<TablePageResult> LoadTablePageAsync(
        string connectionString,
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null)
    {
        var query = BuildTablePageQuery(tableName, orderByColumns, pageSize, pageIndex);
        var result = await ExecuteQueryAsync(connectionString, query, options);
        return TablePageResult.FromQuery(result, pageIndex, pageSize);
    }

    public TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildLimitOffsetPage(Capabilities, tableName, spec);

    public TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildCountQuery(Capabilities, tableName, spec);

    public QueryResult NormalizeExplainResult(QueryResult raw) => raw;

    private string BuildOrderClause(IReadOnlyList<string> orderByColumns)
    {
        if (orderByColumns.Count == 0)
            return string.Empty;

        var ordered = string.Join(", ", orderByColumns.Select(Capabilities.QuoteIdentifier));
        return $" ORDER BY {ordered}";
    }

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(Capabilities.QuoteIdentifier));

    public string? GetActiveDatabaseLabel(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return builder.Database;
        }
        catch { return null; }
    }
}
