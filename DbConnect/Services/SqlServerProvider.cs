using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>RAIN-103 — Microsoft SQL Server provider via Microsoft.Data.SqlClient.</summary>
public partial class SqlServerProvider : DatabaseProviderRoles, IDatabaseProvider
{
    public SqlServerProvider()
    {
        Routine = new SqlServerRoutineProvider(this);
    }

    protected override IDatabaseSchemaProvider Schema { get; } = new SqlServerSchemaProvider();
    protected override IDatabaseRoutineProvider Routine { get; }

    public string ProviderName => "SQL Server";
    public string ProviderIcon => "🔷";
    public string ExplainPrefix => "SET SHOWPLAN_ALL ON;";
    public DatabaseProviderCapabilities Capabilities => ProviderCapabilityDeclarations.SqlServer;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) =>
        ConnectionTester.RunAsync(async () =>
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = 5
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return $"SQL Server {connection.ServerVersion}";
        }, SqlServerConnectionFailures.Classify);

    public async Task<string> GetServerVersionAsync(string connectionString)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand("SELECT @@VERSION;", connection);
            var version = (await cmd.ExecuteScalarAsync())?.ToString() ?? "unknown";
            // @@VERSION is very verbose — trim to first line
            var firstLine = version.Split('\n')[0].Trim();
            return firstLine;
        }
        catch
        {
            return "SQL Server (version unknown)";
        }
    }

    public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        // A session owns its connection outright, so it never borrows one from the pool.
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false };
        return await ConnectionDbSession.OpenAsync(
            this,
            new SqlConnection(builder.ConnectionString),
            (conn, transaction, query, options) => ExecuteQueryCoreAsync(null, conn, transaction, query, options),
            (conn, transaction, query, options) => ExecuteQueryMultipleCoreAsync(null, conn, transaction, query, options),
            cancellationToken,
            configureParameter: BindDateTimeAsDateTime2,
            queryExportCancellation: (_, command, token) => token.Register(command.Cancel));
    }

    /// <summary>
    /// SqlClient sends a DateTime as the legacy datetime type, which keeps about 3 ms, so a datetime2 value written
    /// through a parameter would lose its fraction; datetime2 keeps it, and converts to datetime where a column is one.
    /// </summary>
    internal static void BindDateTimeAsDateTime2(DbParameter parameter)
    {
        if (parameter is SqlParameter { Value: DateTime } sql) sql.SqlDbType = SqlDbType.DateTime2;
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
        SqlConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var stopwatch = Stopwatch.StartNew();
        var notices = new List<string>();
        var results = new List<QueryResult>();
        try
        {
            await using var lease = await ConnectionLease<SqlConnection>.AcquireAsync(
                sharedConnection, () => new SqlConnection(connectionString), options?.CancellationToken ?? default);
            var connection = lease.Connection;
            using var collector = new QueryNoticeCollector(connection, notices);
            await using var cmd = new SqlCommand(query, connection);
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value);
            if (transaction != null)
                cmd.Transaction = (SqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            // KeyInfo runs the query in browse mode, the only mode in which SQL Server sends each column's
            // source table/column (ColumnInfo.SourceTable/SourceColumn). Browse mode may append hidden key
            // columns; DbReaderResults leaves those out.
            await using var reader = await cmd.ExecuteReaderAsync(ReaderBehavior(query), options?.CancellationToken ?? default);
            await DbReaderResults.ReadAllAsync(reader, options, results);
            QueryResultBatchMetadata.Apply(results, query, stopwatch.Elapsed, notices);
            return results;
        }
        catch (Exception ex) when (QueryCancellation.IsCancellation(ex, options, IsCancelInterruption))
        {
            return QueryResultBatchMetadata.Failed(results, query, QueryCancellation.CancelledError(ProviderName), stopwatch.Elapsed, notices);
        }
        catch (SqlException ex)
        {
            return QueryResultBatchMetadata.Failed(results, query, new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message,
                Code = ex.Number.ToString(),
                Position = QueryErrorPositionExtractor.SqlServer(ex.LineNumber),
                IsTimeout = ex.Number == -2
            }, stopwatch.Elapsed, notices);
        }
        catch (Exception ex)
        {
            return QueryResultBatchMetadata.Failed(results, query, new QueryErrorInfo { Provider = ProviderName, Summary = ex.Message, Detail = ex.Message }, stopwatch.Elapsed, notices);
        }
    }

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection.</summary>
    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string? connectionString,
        SqlConnection? sharedConnection,
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
            await using var lease = await ConnectionLease<SqlConnection>.AcquireAsync(
                sharedConnection, () => new SqlConnection(connectionString), options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = new SqlCommand(query, connection);
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value);
            if (transaction != null)
                cmd.Transaction = (SqlTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            // KeyInfo runs the query in browse mode, the only mode in which SQL Server sends each column's
            // source table/column (ColumnInfo.SourceTable/SourceColumn). Browse mode may append hidden key
            // columns; DbReaderResults leaves those out.
            await using var reader = await cmd.ExecuteReaderAsync(ReaderBehavior(query), options?.CancellationToken ?? default);
            await DbReaderResults.ReadAsync(reader, result, options);
        }
        catch (Exception ex) when (QueryCancellation.IsCancellation(ex, options, IsCancelInterruption))
        {
            result.Error = QueryCancellation.CancelledError(ProviderName);
        }
        catch (SqlException ex)
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message,
                Code = ex.Number.ToString(),
                Position = QueryErrorPositionExtractor.SqlServer(ex.LineNumber),
                IsTimeout = ex.Number == -2
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

    /// <summary>
    /// Browse mode puts SET NO_BROWSETABLE ON in front of the text, and CREATE PROCEDURE, VIEW, TRIGGER and
    /// FUNCTION must come first in their batch. A later SELECT in a batch starting with CREATE or ALTER loses its source column details.
    /// </summary>
    private static CommandBehavior ReaderBehavior(string query)
    {
        foreach (var token in SqlTokenizer.EnumerateRanges(query, SqlBatchDialect.SqlServer))
        {
            if (token.Kind == SqlTokenKind.Comment) continue;
            var word = query.AsSpan(token.Start, token.Length);
            return word.Equals("CREATE", StringComparison.OrdinalIgnoreCase) || word.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
                ? CommandBehavior.Default
                : CommandBehavior.KeyInfo;
        }
        return CommandBehavior.KeyInfo;
    }

    /// <summary>
    /// SQL Server reports a cancelled running command as a SqlException numbered 0
    /// ("A severe error occurred on the current command"), not as OperationCanceledException.
    /// Every genuine server error carries a nonzero number, so a real failure is never mistaken for a cancel.
    /// </summary>
    private static bool IsCancelInterruption(Exception exception) =>
        exception is SqlException { Number: 0 };

    public async Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements)
    {
        var commands = statements
            .Select(statement => new SqlCommandSpec(statement, new Dictionary<string, object?>()))
            .ToList();
        return await ExecuteTransactionAsync(connectionString, commands);
    }

    public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) =>
        TransactionExecution.OpenAndExecuteAsync(() => new SqlConnection(connectionString), commands);

    [GeneratedRegex(@"^\s*SELECT\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSelect();

    /// <summary>SQL Server uses TOP N rather than LIMIT.</summary>
    public string FormatLimitQuery(string query, int limit)
    {
        // This rewrites the text to add TOP; it does not decide whether a statement returns rows.
        var select = LeadingSelect().Match(query);
        return select.Success
            ? "SELECT TOP " + limit + " " + query[select.Length..].TrimEnd(';') + ";"
            : query;
    }

    public string BuildTablePageQuery(string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex)
    {
        var offset = Math.Max(pageIndex, 0) * pageSize;
        var quotedTable = QuoteQualifiedIdentifier(tableName);
        var orderClause = BuildOrderClause(orderByColumns);
        return $"SELECT * FROM {quotedTable}{orderClause} OFFSET {offset} ROWS FETCH NEXT {pageSize + 1} ROWS ONLY;";
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

    /// <summary>SQL Server paginates with OFFSET/FETCH rather than LIMIT/OFFSET.</summary>
    public TablePageQuery BuildTablePageQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildOffsetFetchPage(Capabilities, tableName, spec);

    public TablePageQuery BuildTableCountQuery(string tableName, TableQuerySpec spec) =>
        TableQuerySql.BuildCountQuery(Capabilities, tableName, spec);

    public QueryResult NormalizeExplainResult(QueryResult raw)
    {
        if (raw == null) return new QueryResult();
        if (raw.HasError) return raw;

        // Columns to include
        var targetCols = new[] { "StmtText", "PhysicalOp", "LogicalOp", "EstimateRows", "TotalSubtreeCost", "Warnings" };
        var result = new QueryResult();
        for (var i = 0; i < targetCols.Length; i++)
            result.ColumnInfos.Add(new ColumnInfo { Name = targetCols[i], Ordinal = i });

        foreach (var row in raw.ObjectRows)
        {
            var values = new object?[targetCols.Length];
            for (var i = 0; i < targetCols.Length; i++)
                values[i] = raw.ValueOrDefault(row, targetCols[i]);
            result.ObjectRows.Add(values);
        }

        return result;
    }

    private string BuildOrderClause(IReadOnlyList<string> orderByColumns)
    {
        if (orderByColumns.Count == 0)
            return " ORDER BY (SELECT 1)";

        var ordered = string.Join(", ", orderByColumns.Select(Capabilities.QuoteIdentifier));
        return $" ORDER BY {ordered}";
    }

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(Capabilities.QuoteIdentifier));

    public string? GetActiveDatabaseLabel(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(builder.InitialCatalog) ? null : builder.InitialCatalog;
        }
        catch { return null; }
    }
}
