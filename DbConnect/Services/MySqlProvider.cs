using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>P4-010 — MySQL / MariaDB provider via MySqlConnector.</summary>
public class MySqlProvider : DatabaseProviderRoles, IDatabaseProvider
{
    public MySqlProvider()
    {
        Routine = new MySqlRoutineProvider(this);
    }

    protected override IDatabaseSchemaProvider Schema { get; } = new MySqlSchemaProvider();
    protected override IDatabaseRoutineProvider Routine { get; }
    protected override IDatabaseAdministrationProvider Administration { get; } = new MySqlAdministrationProvider();

    public string ProviderName => "MySQL";
    public string ProviderIcon => "🐬";
    public string ExplainPrefix => "EXPLAIN";

    public DatabaseProviderCapabilities Capabilities => ProviderCapabilityDeclarations.MySql;

    // ── Connection string helpers ─────────────────────────────────────────────

    public static string BuildConnectionString(ConnectionInfo info)
    {
        var builder = new MySqlConnectionStringBuilder(info.ConnectionString)
        {
            AllowUserVariables = true
        };
        if (!string.IsNullOrEmpty(info.SslMode) &&
            Enum.TryParse<MySqlSslMode>(info.SslMode, ignoreCase: true, out var sslMode))
        {
            builder.SslMode = sslMode;
        }
        return builder.ConnectionString;
    }

    // ── Core interface ────────────────────────────────────────────────────────

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) =>
        ConnectionTester.RunAsync(async () =>
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                ConnectionTimeout = 5,
                AllowUserVariables = true
            };
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return $"MySQL {connection.ServerVersion}";
        }, MySqlConnectionFailures.Classify);

    public async Task<string> GetServerVersionAsync(string connectionString)
    {
        try
        {
            await using var connection = MySqlConnections.Create(connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT VERSION()";
            var version = await cmd.ExecuteScalarAsync();
            return $"MySQL {version}";
        }
        catch
        {
            return "MySQL (version unknown)";
        }
    }

    public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        // A session owns its connection outright, so it never borrows one from the pool.
        var builder = new MySqlConnectionStringBuilder(connection.ConnectionString)
        {
            AllowUserVariables = true,
            Pooling = false
        };
        return await ConnectionDbSession.OpenAsync(
            this,
            new MySqlConnection(builder.ConnectionString),
            (conn, transaction, query, options) => ExecuteQueryCoreAsync(null, conn, transaction, query, options),
            (conn, transaction, query, options) => ExecuteQueryMultipleCoreAsync(null, conn, transaction, query, options),
            cancellationToken,
            queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
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
        MySqlConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var lease = await ConnectionLease<MySqlConnection>.AcquireAsync(
                sharedConnection,
                () => MySqlConnections.Create(connectionString ?? throw new ArgumentNullException(nameof(connectionString))),
                options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value);
            if (transaction != null)
                cmd.Transaction = (MySqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(options?.CancellationToken ?? default);
            var results = await DbReaderResults.ReadAllAsync(reader, options);
            foreach (var result in results)
            {
                result.ExecutedSql = query;
                result.Duration = stopwatch.Elapsed;
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            return [MultiResultError(query, QueryCancellation.CancelledError(ProviderName), stopwatch.Elapsed)];
        }
        catch (MySqlException ex)
        {
            return [MultiResultError(query, new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message,
                Code = ex.Number.ToString(), Position = QueryErrorPositionExtractor.MySql(ex.Message)
            }, stopwatch.Elapsed)];
        }
        catch (Exception ex)
        {
            return [MultiResultError(query, new QueryErrorInfo { Provider = ProviderName, Summary = ex.Message, Detail = ex.Message }, stopwatch.Elapsed)];
        }
    }

    private static QueryResult MultiResultError(string query, QueryErrorInfo error, TimeSpan duration) =>
        new() { ExecutedSql = query, Error = error, Duration = duration };

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection.</summary>
    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string? connectionString,
        MySqlConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var result = new QueryResult { ExecutedSql = query };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var lease = await ConnectionLease<MySqlConnection>.AcquireAsync(
                sharedConnection,
                () => MySqlConnections.Create(connectionString ?? throw new ArgumentNullException(nameof(connectionString))),
                options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value);
            if (transaction != null)
                cmd.Transaction = (MySqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(options?.CancellationToken ?? default);
            await DbReaderResults.ReadAsync(reader, result, options);
        }
        // MySqlConnector reports a killed statement ("Query execution was interrupted") as
        // OperationCanceledException, so no driver-specific rule is needed.
        catch (OperationCanceledException)
        {
            result.Error = QueryCancellation.CancelledError(ProviderName);
        }
        catch (MySqlException ex)
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message,
                Code = ex.Number.ToString(),
                Position = QueryErrorPositionExtractor.MySql(ex.Message)
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

    public async Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = MySqlConnections.Create(connectionString);
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
        TransactionExecution.OpenAndExecuteAsync(() => MySqlConnections.Create(connectionString), commands);

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
        if (orderByColumns.Count == 0) return string.Empty;
        var ordered = string.Join(", ", orderByColumns.Select(Capabilities.QuoteIdentifier));
        return $" ORDER BY {ordered}";
    }

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(Capabilities.QuoteIdentifier));

    public string? GetActiveDatabaseLabel(string connectionString)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(builder.Database) ? null : builder.Database;
        }
        catch { return null; }
    }
}
