using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// A canned two-column (Id, Name) Customers table and a fake provider that pages it, shared by the
/// table-data view model tests and the headless table-data view tests.
/// </summary>
internal static class TableDataTestFixtures
{
    internal static DatabaseSession CreateSession(TablePagingProvider provider) =>
        new(
            new ConnectionInfo
            {
                Name = "Local",
                Provider = provider.ProviderName,
                ConnectionString = "Data Source=:memory:"
            },
            provider);

    internal static SchemaNode CreateTableNode(bool hasPrimaryKey)
    {
        var table = new SchemaNode
        {
            Name = "Customers",
            RawName = "Customers",
            Type = SchemaNodeType.Table
        };
        table.Children.Add(new SchemaNode
        {
            Name = "Id",
            RawName = "Id",
            Type = SchemaNodeType.Column,
            IsPrimaryKey = hasPrimaryKey
        });
        table.Children.Add(new SchemaNode
        {
            Name = "Name",
            RawName = "Name",
            Type = SchemaNodeType.Column
        });
        return table;
    }

    internal static TablePageResult CreatePage(IReadOnlyList<int> ids, bool hasNextPage = false) =>
        new()
        {
            Result = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }],
                ObjectRows = ids.Select(id => new object?[] { (long)id, $"Name {id}" }).ToList()
            },
            HasNextPage = hasNextPage
        };

    // Id/Name plus one column of each classified editor type.
    internal static TablePageResult CreateTypedPage() =>
        new()
        {
            Result = new QueryResult
            {
                ColumnInfos =
                [
                    new ColumnInfo { Name = "Id", Ordinal = 0, ClrType = typeof(long) },
                    new ColumnInfo { Name = "Name", Ordinal = 1, ClrType = typeof(string) },
                    new ColumnInfo { Name = "IsActive", Ordinal = 2, ClrType = typeof(bool) },
                    new ColumnInfo { Name = "BirthDate", Ordinal = 3, ProviderTypeName = "DATE" },
                    new ColumnInfo { Name = "CreatedAt", Ordinal = 4, ClrType = typeof(DateTime) },
                    new ColumnInfo { Name = "Price", Ordinal = 5, ClrType = typeof(decimal) },
                ],
                ObjectRows =
                [
                    new object?[] { 1L, "Widget", true, new DateTime(2020, 1, 1), new DateTime(2026, 9, 27, 10, 0, 0), 9.99m }
                ]
            }
        };

    internal sealed class TablePagingProvider(
        TablePageResult firstPage,
        TablePageResult? secondPage = null,
        Dictionary<string, EnumColumnInfo>? enumColumns = null) : IDatabaseProvider
    {
        public int PageLoads { get; private set; }
        public int QueryExecutions { get; private set; }
        public string ProviderName => "Recording";
        public string ProviderIcon => "R";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT");
        public string ExplainPrefix => "EXPLAIN";
        public IReadOnlyList<string> LastOrderByColumns { get; private set; } = [];
        public IReadOnlyList<SqlCommandSpec> TransactionCommands { get; private set; } = [];

        public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, System.Threading.CancellationToken ct = default) =>
            Task.FromResult(enumColumns ?? new Dictionary<string, EnumColumnInfo>());

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("Recording");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) { QueryExecutions++; return Task.FromResult(new QueryResult()); }

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }

        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null)
        {
            PageLoads++;
            LastOrderByColumns = orderByColumns.ToList();
            var page = pageIndex == 0 ? firstPage : secondPage ?? new TablePageResult
            {
                Result = new QueryResult { ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Name", Ordinal = 1 }], ObjectRows = [] }
            };

            return Task.FromResult(new TablePageResult
            {
                Result = new QueryResult
                {
                    ColumnInfos = page.Result.ColumnInfos.ToList(),
                    ObjectRows = page.Result.ObjectRows.Select(row => (object?[])row.Clone()).ToList()
                },
                PageIndex = pageIndex,
                PageSize = pageSize,
                HasNextPage = page.HasNextPage
            });
        }

        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(0));

        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands)
        {
            TransactionCommands = commands.ToList();
            return Task.FromResult(TransactionResult.Success(commands.Count));
        }

        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => ["text"];

        // TableDataTabViewModel always loads through a TableQuerySpec; this fake's session
        // translates that spec back into the canned orderByColumns-based page lookup above,
        // rather than implementing BuildTablePageQuery/ExecuteQueryAsync SQL rendering.
        public Task<IDbSession> OpenSessionAsync(ConnectionInfo connection, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbSession>(new RecordingSession(this));

        private sealed class RecordingSession(TablePagingProvider provider) : IDbSession
        {
            public IDatabaseProvider Provider { get; } = provider;
            public bool IsInTransaction => false;
            public Task BeginTransactionAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task CommitAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task RollbackAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task CancelAsync() => Task.CompletedTask;

            public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) =>
                provider.ExecuteQueryAsync(string.Empty, query, options);

            public Task<TablePageResult> LoadTablePageAsync(
                string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
                provider.LoadTablePageAsync(string.Empty, tableName, orderByColumns, pageSize, pageIndex, options);

            public Task<TablePageResult> LoadTablePageAsync(string tableName, TableQuerySpec spec, QueryExecutionOptions? options = null) =>
                provider.LoadTablePageAsync(string.Empty, tableName, spec.Sort.Select(k => k.Column).ToList(), spec.PageSize, spec.PageIndex, options);

            public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) =>
                provider.ExecuteTransactionAsync(string.Empty, commands);

            public System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
                string query, QueryExecutionOptions? options = null, System.Threading.CancellationToken cancellationToken = default) =>
                provider.ExecuteQueryMultipleAsync(string.Empty, query, options, cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
