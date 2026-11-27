using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>P4-020 — DuckDB provider via DuckDB.NET.Data.</summary>
public class DuckDbProvider : DatabaseProviderRoles, IDatabaseProvider
{
    public DuckDbProvider()
    {
        Routine = new DuckDbRoutineProvider(this);
    }

    protected override IDatabaseSchemaProvider Schema { get; } = new DuckDbSchemaProvider();
    protected override IDatabaseRoutineProvider Routine { get; }

    public string ProviderName => "DuckDB";
    public string ProviderIcon => "🦆";
    public string ExplainPrefix => "EXPLAIN";

    public DatabaseProviderCapabilities Capabilities => ProviderCapabilityDeclarations.DuckDb;

    // ── Core interface ────────────────────────────────────────────────────────

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) =>
        ConnectionTester.RunAsync(async () =>
        {
            await using var connection = DuckDbConnections.Create(connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version()";
            return $"DuckDB {await cmd.ExecuteScalarAsync()}";
        }, DuckDbConnectionFailures.Classify);

    public async Task<string> GetServerVersionAsync(string connectionString)
    {
        try
        {
            await using var connection = DuckDbConnections.Create(connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version()";
            var version = await cmd.ExecuteScalarAsync();
            return version?.ToString() ?? "DuckDB (version unknown)";
        }
        catch
        {
            return "DuckDB (version unknown)";
        }
    }

    public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        return await ConnectionDbSession.OpenAsync(
            this,
            DuckDbConnections.Create(connection.ConnectionString),
            (conn, transaction, query, options) => ExecuteQueryCoreAsync(null, conn, transaction, query, options),
            (conn, transaction, query, options) => ExecuteQueryMultipleCoreAsync(null, conn, transaction, query, options),
            cancellationToken,
            namedParameters: false,
            queryExportCancellation: (_, command, token) => DuckDbQueryExport.Register(command, token),
            queryExportCompletion: DuckDbQueryExport.ValidateCompletion);
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

    /// <summary>DuckDB.NET does not traverse multiple result sets from one command via NextResultAsync,
    /// so a multi-statement batch is split textually and run as separate round trips.</summary>
    private async Task<List<QueryResult>> ExecuteQueryMultipleCoreAsync(
        string? connectionString,
        DuckDBConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var statements = SqlBatchParser.SplitStatements(query);
        var results = new List<QueryResult>(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            if (options?.CancellationToken.IsCancellationRequested == true)
                break;

            // Only the last statement may keep a capped reader open; the others would wait on it.
            var statementOptions = i == statements.Count - 1 || options?.Continuation == null ? options : options with { Continuation = null };
            results.Add(await ExecuteQueryCoreAsync(connectionString, sharedConnection, transaction, statements[i], statementOptions));
        }

        return results;
    }

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection.</summary>
    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string? connectionString,
        DuckDBConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var result = new QueryResult { ExecutedSql = query };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var lease = await ConnectionLease<DuckDBConnection>.AcquireAsync(
                sharedConnection,
                () => DuckDbConnections.Create(connectionString ?? throw new ArgumentNullException(nameof(connectionString))),
                options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            foreach (var value in options?.Parameters ?? [])
                cmd.Parameters.Add(new DuckDBParameter { Value = value ?? DBNull.Value });
            if (transaction != null)
                cmd.Transaction = (DuckDBTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(options?.CancellationToken ?? default);
            await DbReaderResults.ReadAsync(reader, result, options);
        }
        catch (OperationCanceledException)
        {
            result.Error = QueryCancellation.CancelledError(ProviderName);
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

    public async Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements)
    {
        var commands = statements
            .Select(s => new SqlCommandSpec(s, new Dictionary<string, object?>()))
            .ToList();
        return await ExecuteTransactionAsync(connectionString, commands);
    }

    public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) =>
        TransactionExecution.OpenAndExecuteAsync(() => DuckDbConnections.Create(connectionString), commands, namedParameters: false);

    public string FormatLimitQuery(string query, int limit) =>
        $"{query.TrimEnd(';')} LIMIT {limit};";

    public string BuildTablePageQuery(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex)
    {
        var offset = Math.Max(pageIndex, 0) * pageSize;
        var quotedTable = QuoteQualifiedIdentifier(tableName);
        var orderClause = BuildOrderClause(orderByColumns);
        return $"SELECT * FROM {quotedTable}{orderClause} LIMIT {pageSize + 1} OFFSET {offset};";
    }

    public TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildLimitOffsetPage(Capabilities, tableName, spec);

    public TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildCountQuery(Capabilities, tableName, spec);

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

    public QueryResult NormalizeExplainResult(QueryResult raw) => raw;

    private string BuildOrderClause(IReadOnlyList<string> orderByColumns)
    {
        if (orderByColumns.Count == 0) return string.Empty;
        var ordered = string.Join(", ", orderByColumns.Select(Capabilities.QuoteIdentifier));
        return $" ORDER BY {ordered}";
    }

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(Capabilities.QuoteIdentifier));
}
