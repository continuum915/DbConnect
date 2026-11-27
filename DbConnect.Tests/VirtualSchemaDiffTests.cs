using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

public class VirtualSchemaDiffTests
{
    [Fact]
    public async Task DiffVirtualSchema_NewTable_ProducesCreateItem()
    {
        var service = new SchemaDiffService();
        var session = CreateSession(new StubProvider());
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable
                {
                    Name = "customers",
                    Columns = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } }
                }
            }
        };

        var diff = await service.DiffVirtualSchemaAsync(schema, session);

        var item = Assert.Single(diff);
        Assert.Equal("customers", item.ObjectName);
        Assert.Equal(SchemaDiffChangeType.Add, item.ChangeType);
    }

    [Fact]
    public async Task DiffVirtualSchema_ExistingTable_NoChange_ProducesNoItems()
    {
        var provider = new StubProvider(
            schemaNodes: new List<SchemaNode> { new() { Name = "customers", Type = SchemaNodeType.Table } },
            columnsByTable: new Dictionary<string, List<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } }
            });
        var service = new SchemaDiffService();
        var session = CreateSession(provider);
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable
                {
                    Name = "customers",
                    Columns = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } }
                }
            }
        };

        var diff = await service.DiffVirtualSchemaAsync(schema, session);

        Assert.Empty(diff);
    }

    [Fact]
    public async Task DiffVirtualSchema_AddedColumn_ProducesAlterItem()
    {
        var provider = new StubProvider(
            schemaNodes: new List<SchemaNode> { new() { Name = "customers", Type = SchemaNodeType.Table } },
            columnsByTable: new Dictionary<string, List<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } }
            });
        var service = new SchemaDiffService();
        var session = CreateSession(provider);
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable
                {
                    Name = "customers",
                    Columns = new()
                    {
                        new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false },
                        new ColumnDefinition { Name = "name", DataType = "TEXT", IsNullable = true }
                    }
                }
            }
        };

        var diff = await service.DiffVirtualSchemaAsync(schema, session);

        var item = Assert.Single(diff);
        Assert.Equal(SchemaDiffChangeType.Modify, item.ChangeType);
        Assert.Equal("customers", item.ObjectName);
    }

    [Fact]
    public async Task DiffVirtualSchema_NewRelationship_ProducesForeignKeyCreateItem()
    {
        var provider = new StubProvider(
            schemaNodes: new List<SchemaNode>
            {
                new() { Name = "orders", Type = SchemaNodeType.Table },
                new() { Name = "customers", Type = SchemaNodeType.Table }
            },
            columnsByTable: new Dictionary<string, List<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["orders"] = new() { new ColumnDefinition { Name = "customer_id", DataType = "INTEGER", IsNullable = false } },
                ["customers"] = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } }
            });
        var service = new SchemaDiffService();
        var session = CreateSession(provider);
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable { Name = "orders", Columns = new() { new ColumnDefinition { Name = "customer_id", DataType = "INTEGER", IsNullable = false } } },
                new VirtualTable { Name = "customers", Columns = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false } } }
            },
            Relationships = new()
            {
                new VirtualRelationship { FromTable = "orders", FromColumn = "customer_id", ToTable = "customers", ToColumn = "id" }
            }
        };

        var diff = await service.DiffVirtualSchemaAsync(schema, session);

        var item = Assert.Single(diff, entry => entry.ObjectType == "foreign key");
        Assert.Equal(SchemaDiffChangeType.Add, item.ChangeType);
        Assert.Contains("FOREIGN KEY", item.GeneratedScript, StringComparison.OrdinalIgnoreCase);
    }

    private static DatabaseSession CreateSession(IDatabaseProvider provider) => new(
        new ConnectionInfo { Name = "Test", Provider = provider.ProviderName, ConnectionString = "Data Source=test.db" },
        provider);

    private sealed class StubProvider(
        List<SchemaNode>? schemaNodes = null,
        Dictionary<string, List<ColumnDefinition>>? columnsByTable = null,
        List<object?[]>? foreignKeys = null) : IDatabaseProvider
    {
        public string ProviderName => "SQLite";
        public string ProviderIcon => "S";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT") { SupportsForeignKeys = true };
        public string ExplainPrefix => "EXPLAIN";

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("Test");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(schemaNodes ?? new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }

        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null)
        {
            if (query.StartsWith("PRAGMA foreign_key_list", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new QueryResult
                {
                    ObjectRows = foreignKeys?.ToList() ?? new List<object?[]>()
                });
            }

            return Task.FromResult(new QueryResult());
        }

        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult { Result = new QueryResult(), PageIndex = pageIndex, PageSize = pageSize });

        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(statements.Count));
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(commands.Count));
        public string FormatLimitQuery(string query, int limit) => $"{query} LIMIT {limit}";
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) =>
            Task.FromResult(columnsByTable != null && columnsByTable.TryGetValue(tableName, out var columns) ? columns : new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => new() { "INTEGER", "TEXT" };
    }
}
