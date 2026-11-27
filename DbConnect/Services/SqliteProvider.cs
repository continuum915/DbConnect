using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

public class SqliteProvider : DatabaseProviderRoles, IDatabaseProvider, ITableDefinitionProvider, ITableStatisticsSource
{
    public Task<IReadOnlyList<TableStatistics>> GetTableStatisticsAsync(string connectionString, CancellationToken cancellationToken = default) =>
        SqliteTableStatistics.ReadAsync(connectionString, cancellationToken);

    public SqliteProvider()
    {
        Routine = new SqliteRoutineProvider(this);
    }

    protected override IDatabaseSchemaProvider Schema { get; } = new SqliteSchemaProvider();
    protected override IDatabaseRoutineProvider Routine { get; }

    public string ProviderName => "SQLite";
    public string ProviderIcon => "🟠";
    public string ExplainPrefix => "EXPLAIN QUERY PLAN";
    public DatabaseProviderCapabilities Capabilities => ProviderCapabilityDeclarations.Sqlite;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) =>
        ConnectionTester.RunAsync(async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            return $"SQLite {connection.ServerVersion}";
        }, SqliteConnectionFailures.Classify);

    public async Task<string> GetServerVersionAsync(string connectionString)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version();";
            var version = await cmd.ExecuteScalarAsync();
            return $"SQLite {version}";
        }
        catch
        {
            return "SQLite (version unknown)";
        }
    }

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

    /// <summary>
    /// Microsoft.Data.Sqlite does not traverse multiple result sets from one command via
    /// NextResultAsync, so a multi-statement batch is split textually and run as separate round trips.
    /// </summary>
    private async Task<List<QueryResult>> ExecuteQueryMultipleCoreAsync(
        string? connectionString,
        SqliteConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var statements = SqlBatchParser.SplitStatements(query, Capabilities.BatchDialect);
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

    public async Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        // Pooled connections would leak temp tables and settings between sessions.
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString) { Pooling = false };
        return await ConnectionDbSession.OpenAsync(
            this,
            new SqliteConnection(builder.ConnectionString),
            (conn, transaction, query, options) => ExecuteQueryCoreAsync(null, conn, transaction, query, options),
            (conn, transaction, query, options) => ExecuteQueryMultipleCoreAsync(null, conn, transaction, query, options),
            cancellationToken, queryExportCancellation: (conn, _, token) => new SqliteExportCancellation(conn, token));
    }

    public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) =>
        ExecuteQueryCoreAsync(connectionString, null, null, query, options);

    /// <summary>Runs on <paramref name="sharedConnection"/> when given, otherwise on a fresh connection.</summary>
    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string? connectionString,
        SqliteConnection? sharedConnection,
        DbTransaction? transaction,
        string query,
        QueryExecutionOptions? options)
    {
        var result = new QueryResult { ExecutedSql = query };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var lease = await ConnectionLease<SqliteConnection>.AcquireAsync(
                sharedConnection, () => new SqliteConnection(connectionString), options?.CancellationToken ?? default);
            var connection = lease.Connection;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            for (var i = 0; i < (options?.Parameters?.Count ?? 0); i++)
                cmd.Parameters.AddWithValue($"@p{i}", options!.Parameters![i] ?? DBNull.Value);
            if (transaction != null)
                cmd.Transaction = (SqliteTransaction)transaction;
            if (options?.TimeoutSeconds > 0)
                cmd.CommandTimeout = options.TimeoutSeconds;

            using var cancellation = new SqliteExportCancellation(connection, options?.CancellationToken ?? default);
            await using var reader = await cmd.ExecuteReaderAsync(options?.CancellationToken ?? default);
            await DbReaderResults.ReadAsync(reader, result, options);
        }
        catch (OperationCanceledException)
        {
            result.Error = QueryCancellation.CancelledError(ProviderName);
        }
        catch (SqliteException ex) when (options?.TimeoutSeconds > 0 && ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = "The query timed out.",
                Detail = ex.Message,
                Code = ex.SqliteErrorCode.ToString(),
                Position = QueryErrorPositionExtractor.Sqlite(ex.Message, query),
                IsTimeout = true
            };
        }
        catch (SqliteException ex)
        {
            result.Error = new QueryErrorInfo
            {
                Provider = ProviderName,
                Summary = ex.Message,
                Detail = ex.Message,
                Code = ex.SqliteErrorCode.ToString(),
                Position = QueryErrorPositionExtractor.Sqlite(ex.Message, query)
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
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        if (sql.StartsWith(SqliteRebuildExecution.Marker, StringComparison.Ordinal))
            return await SqliteRebuildExecution.ExecuteAsync(connection, sql);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
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
        TransactionExecution.OpenAndExecuteAsync(() => new SqliteConnection(connectionString), commands);

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

    public QueryResult NormalizeExplainResult(QueryResult raw)
    {
        if (raw == null) return new QueryResult();
        if (raw.HasError) return raw;

        // Columns typically: id, parent, notused, detail
        int idIdx = raw.IndexOfColumn("id");
        int parentIdx = raw.IndexOfColumn("parent");
        int detailIdx = raw.IndexOfColumn("detail");

        if (idIdx == -1 || parentIdx == -1 || detailIdx == -1)
        {
            return raw;
        }

        var result = new QueryResult
        {
            ColumnInfos =
            {
                new ColumnInfo { Name = "Step", Ordinal = 0 },
                new ColumnInfo { Name = "Node", Ordinal = 1 }
            }
        };

        var nodes = new Dictionary<long, SqlitePlanNode>();
        var rootNodes = new List<SqlitePlanNode>();

        foreach (var row in raw.ObjectRows)
        {
            long id = Convert.ToInt64(row[idIdx]);
            long parent = Convert.ToInt64(row[parentIdx]);
            string detail = row[detailIdx]?.ToString() ?? "";

            var node = new SqlitePlanNode { Id = id, ParentId = parent, Detail = detail };
            nodes[id] = node;
        }

        foreach (var node in nodes.Values)
        {
            if (node.ParentId != 0 && nodes.TryGetValue(node.ParentId, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                rootNodes.Add(node);
            }
        }

        foreach (var root in rootNodes)
        {
            WalkSqliteTree(root, 0, result);
        }

        return result;
    }

    private class SqlitePlanNode
    {
        public long Id { get; set; }
        public long ParentId { get; set; }
        public string Detail { get; set; } = "";
        public List<SqlitePlanNode> Children { get; } = new();
    }

    private static void WalkSqliteTree(SqlitePlanNode node, int depth, QueryResult result)
    {
        string indent = new string(' ', depth * 4);
        result.ObjectRows.Add(new object?[] { indent + node.Detail, node.Id });

        foreach (var child in node.Children)
        {
            WalkSqliteTree(child, depth + 1, result);
        }
    }

    public Task<TableRebuildDefinition> GetTableDefinitionAsync(string connectionString, string tableName) =>
        SqliteSchemaMetadata.LoadDefinitionAsync(connectionString, tableName);

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
            var parts = connectionString.Split(';');
            var dataSource = parts.FirstOrDefault(p =>
                p.TrimStart().StartsWith("Data Source", StringComparison.OrdinalIgnoreCase));
            if (dataSource is null) return null;
            var path = dataSource.Split('=', 2).LastOrDefault()?.Trim();
            if (string.IsNullOrEmpty(path) || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                return ":memory:";
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch { return null; }
    }
}
