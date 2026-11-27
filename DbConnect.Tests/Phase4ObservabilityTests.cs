using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// Tests for P4-030 Connection Monitor, P4-040 EXPLAIN Visualization, P4-050 Slow Query Dashboard.
/// </summary>
public class Phase4ObservabilityTests
{
    // ── ExplainParserServiceTests ─────────────────────────────────────────────

    [Fact]
    public void PostgreSQL_TextPlan_ParsesRootNodeWithCorrectNodeType()
    {
        var service = new ExplainParserService();
        var result = MakeResult(new[]
        {
            "Seq Scan on users  (cost=0.00..1.01 rows=1 width=68)"
        });

        var root = service.ParsePostgreSQL(result);

        Assert.NotNull(root);
        Assert.Contains("Seq Scan", root.NodeType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSQL_NestedPlan_ProducesParentAndChildNodes()
    {
        var service = new ExplainParserService();
        var result = MakeResult(new[]
        {
            "Hash Join  (cost=1.05..2.09 rows=1 width=8)",
            "  ->  Seq Scan on orders  (cost=0.00..1.01 rows=1 width=4)",
            "  ->  Hash  (cost=1.01..1.01 rows=3 width=4)",
            "        ->  Seq Scan on users  (cost=0.00..1.01 rows=3 width=4)"
        });

        var root = service.ParsePostgreSQL(result);

        Assert.NotNull(root);
        Assert.True(root.Children.Count > 0, "Root node should have at least one child");
    }

    [Fact]
    public void MySQL_TabularExplain_ProducesFlatNodeList()
    {
        var service = new ExplainParserService();
        var result = new QueryResult
        {
            ColumnInfos =
            [
                new ColumnInfo { Name = "id", Ordinal = 0 },
                new ColumnInfo { Name = "select_type", Ordinal = 1 },
                new ColumnInfo { Name = "table", Ordinal = 2 },
                new ColumnInfo { Name = "type", Ordinal = 3 },
                new ColumnInfo { Name = "rows", Ordinal = 4 },
                new ColumnInfo { Name = "Extra", Ordinal = 5 }
            ],
            ObjectRows =
            [
                new object?[] { 1, "SIMPLE", "users", "ALL", 100, null },
                new object?[] { 1, "SIMPLE", "orders", "ref", 5, null }
            ]
        };

        var root = service.ParseMySQL(result);

        Assert.NotNull(root);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void ParseOfEmptyResult_ReturnsFallbackGenericNode()
    {
        var service = new ExplainParserService();
        var empty = new QueryResult();

        var node = service.Parse(empty, "PostgreSQL");

        Assert.NotNull(node);
        Assert.Equal("Plan", node.NodeType);
    }

    [Fact]
    public void RelativeCost_IsNormalized_ZeroToOne()
    {
        var service = new ExplainParserService();
        var result = MakeResult(new[]
        {
            "Hash Join  (cost=1.00..10.00 rows=100 width=8)",
            "  ->  Seq Scan on t  (cost=0.00..5.00 rows=50 width=4)",
            "  ->  Hash  (cost=0.00..1.00 rows=10 width=4)"
        });

        var root = service.Parse(result, "PostgreSQL");

        Assert.NotNull(root);
        // Collect all nodes
        var all = new List<ExplainPlanNode>();
        Collect(root, all);

        // At least one node should have RelativeCost == 1.0 (max)
        Assert.Contains(all, n => Math.Abs(n.RelativeCost - 1.0) < 0.001);
        // All costs should be 0–1
        Assert.All(all, n =>
        {
            Assert.InRange(n.RelativeCost, 0.0, 1.0);
        });
    }

    // ── SlowQueryLogServiceTests ──────────────────────────────────────────────

    [Fact]
    public void Fingerprint_StripsStringLiterals()
    {
        var svc = new SlowQueryLogService();
        var fp = svc.Fingerprint("SELECT * FROM users WHERE name = 'Alice'");
        Assert.DoesNotContain("Alice", fp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("?", fp, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_NormalizesWhitespace()
    {
        var svc = new SlowQueryLogService();
        var fp1 = svc.Fingerprint("SELECT   *  FROM   users");
        var fp2 = svc.Fingerprint("SELECT * FROM users");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public async Task AppendAsync_And_LoadEntriesAsync_RoundTrip()
    {
        var svc = new SlowQueryLogService();
        var connName = $"test-{Guid.NewGuid():N}";

        var entry = new SlowQueryEntry
        {
            ConnectionName = connName,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMilliseconds(1500),
            QueryText = "SELECT * FROM users",
            Fingerprint = svc.Fingerprint("SELECT * FROM users"),
            IsError = false,
            RowsAffected = 10
        };

        await svc.AppendAsync(entry);
        var loaded = await svc.LoadEntriesAsync(connName);

        Assert.NotEmpty(loaded);
        Assert.Equal(entry.QueryText, loaded[0].QueryText);
        Assert.Equal(entry.Fingerprint, loaded[0].Fingerprint);

        // Cleanup
        await svc.ClearAsync(connName);
    }

    [Fact]
    public async Task GetAggregates_GroupsByFingerprint()
    {
        var svc = new SlowQueryLogService();
        var connName = $"test-{Guid.NewGuid():N}";
        var fp = svc.Fingerprint("SELECT * FROM orders WHERE id = 42");

        for (int i = 0; i < 3; i++)
        {
            await svc.AppendAsync(new SlowQueryEntry
            {
                ConnectionName = connName,
                Timestamp = DateTimeOffset.UtcNow,
                Duration = TimeSpan.FromMilliseconds(1000 + i * 100),
                QueryText = $"SELECT * FROM orders WHERE id = {i}",
                Fingerprint = fp
            });
        }

        var aggregates = await svc.GetAggregatesAsync(connName);

        Assert.NotEmpty(aggregates);
        var group = Assert.Single(aggregates, a => a.Fingerprint == fp);
        Assert.Equal(3, group.Count);
        Assert.True(group.MaxDuration >= group.AvgDuration);

        // Cleanup
        await svc.ClearAsync(connName);
    }

    // ── ConnectionMonitorServiceTests ─────────────────────────────────────────

    [Fact]
    public async Task GetKillStatement_ForPostgreSQL_ReturnsPgTerminateBackend()
    {
        var svc = new ConnectionMonitorService();
        var session = MakeSession("PostgreSQL");
        var stmt = await svc.GetKillStatementAsync(session, "12345");
        Assert.Contains("pg_terminate_backend", stmt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12345", stmt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetKillStatement_ForEmbeddedProvider_ReturnsComment()
    {
        var svc = new ConnectionMonitorService();
        var session = MakeSession("SQLite");
        var stmt = await svc.GetKillStatementAsync(session, "1");
        Assert.StartsWith("--", stmt.TrimStart(), StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static QueryResult MakeResult(string[] lines)
    {
        var result = new QueryResult { ColumnInfos = [new ColumnInfo { Name = "QUERY PLAN", Ordinal = 0 }] };
        foreach (var line in lines)
            result.ObjectRows.Add(new object?[] { line });
        return result;
    }

    private static void Collect(ExplainPlanNode node, List<ExplainPlanNode> all)
    {
        all.Add(node);
        foreach (var child in node.Children) Collect(child, all);
    }

    private static DatabaseSession MakeSession(string providerName) =>
        new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = providerName },
            new StubProvider(providerName));

    private sealed class StubProvider : IDatabaseProvider
    {
        private readonly string _name;
        public StubProvider(string name) => _name = name;
        public string ProviderName => _name;
        public string ProviderIcon => string.Empty;
        public string ExplainPrefix => "EXPLAIN";
        public DatabaseProviderCapabilities Capabilities => new DatabaseProviderCapabilities("\"", "\"", "LIMIT");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<TablePageResult> LoadTablePageAsync(string cs, string table, IReadOnlyList<string> order, int pageSize, int pageIndex, QueryExecutionOptions? options = null) => Task.FromResult(new TablePageResult());
        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(new TransactionResult());
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(new TransactionResult());
        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public Task<List<string>> GetSchemasAsync(string connectionString) => Task.FromResult(new List<string>());
        public Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName) => Task.FromResult<string?>(null);
        public Task<string?> GetProcedureDefinitionAsync(string connectionString, string schema, string procedureName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => new();
        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult(string.Empty);
    }
}
