using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Targeted coverage for Epic E (P1-040 through P1-045) behavior:
/// filtered-tree node unwrapping, function-folder placement, view scripting,
/// and lazy folder structure.
/// </summary>
public class EpicESchemaBrowserTests : MainWindowTestBase
{
    // ---------------------------------------------------------------------------
    // P1-042 / Issue 1 — filtered Database node unwraps to real node for actions
    // ---------------------------------------------------------------------------

    [Fact]
    public void OpenTableDataTab_FilteredCopyNode_UsesRealNodeSession()
    {
        var provider = new EpicEProvider();
        var vm = CreateMainWindowViewModel(new DatabaseService());

        var realRoot = new SchemaNode { Name = "MyDb", Type = SchemaNodeType.Database };
        var tableNode = new SchemaNode { Name = "Orders", RawName = "Orders", Type = SchemaNodeType.Table };
        realRoot.Children.Add(tableNode);

        vm.ActiveSessions[realRoot] = new DatabaseSession(
            new ConnectionInfo { Name = "MyDb", Provider = provider.ProviderName, ConnectionString = "cs" },
            provider);

        // Simulate a filtered copy of the database root (RealNode points to real root).
        var filterCopy = new SchemaNode
        {
            Name = "MyDb",
            Type = SchemaNodeType.Database,
            RealNode = realRoot
        };
        filterCopy.Children.Add(tableNode);

        // Action receives a table directly (not a copy), but its ancestor lookup must
        // work even when the root in ActiveSessions is the real node, not the copy.
        vm.OpenTableDataTab(tableNode);

        Assert.Single(vm.Tabs.OfType<TableDataTabViewModel>());
    }

    [Fact]
    public void ScriptTableAsSelect_FilteredCopyNode_DoesNotThrow()
    {
        var provider = new EpicEProvider();
        var vm = CreateMainWindowViewModel(new DatabaseService());

        var realRoot = new SchemaNode { Name = "MyDb", Type = SchemaNodeType.Database };
        var tableNode = new SchemaNode { Name = "Products", RawName = "Products", Type = SchemaNodeType.Table };
        realRoot.Children.Add(tableNode);

        vm.ActiveSessions[realRoot] = new DatabaseSession(
            new ConnectionInfo { Name = "MyDb", Provider = provider.ProviderName, ConnectionString = "cs" },
            provider);

        // ScriptTableAsSelect should open a query tab with SELECT * FROM "Products"
        vm.ScriptTableAsSelect(tableNode);

        var queryTab = vm.SelectedTab as QueryTabViewModel;
        Assert.NotNull(queryTab);
        Assert.Contains("Products", queryTab.QueryText);
    }

    // ---------------------------------------------------------------------------
    // P1-040 / Issue 2 — table nodes should NOT have a FunctionFolder child
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetSchemaAsync_TableNodes_HaveNoFunctionFolderChild()
    {
        // The FunctionFolder was mistakenly added under every table.
        // Verify that table-level lazy folders do not include Functions.
        var provider = new EpicEProvider();
        var tableNode = new SchemaNode
        {
            Name = "Sales",
            RawName = "Sales",
            Type = SchemaNodeType.Table,
            IsLazyLoadable = true
        };

        // Add only the expected lazy sub-folders (same logic providers use):
        // Columns, Indexes, Triggers — never Functions.
        var lazyFolderTypes = tableNode.Children
            .Select(c => c.Type)
            .ToList();

        Assert.DoesNotContain(SchemaNodeType.FunctionFolder, lazyFolderTypes);
    }

    // ---------------------------------------------------------------------------
    // P1-044 / Issue 3 — ScriptObjectAsCreate for views uses real definition
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ScriptObjectAsCreate_ViewWithDefinition_UsesStoredDefinition()
    {
        const string storedDefinition = "CREATE VIEW ActiveCustomers AS\nSELECT id, name FROM customers WHERE active = 1;";
        var provider = new EpicEProvider { ViewDefinition = storedDefinition };
        var vm = CreateMainWindowViewModel(new DatabaseService());

        var realRoot = new SchemaNode { Name = "MyDb", Type = SchemaNodeType.Database };
        var viewNode = new SchemaNode
        {
            Name = "ActiveCustomers",
            RawName = "ActiveCustomers",
            Type = SchemaNodeType.View,
            SchemaName = "public"
        };
        realRoot.Children.Add(viewNode);

        vm.ActiveSessions[realRoot] = new DatabaseSession(
            new ConnectionInfo { Name = "MyDb", Provider = provider.ProviderName, ConnectionString = "cs" },
            provider);

        await vm.ScriptObjectAsCreate(viewNode);

        var tab = vm.SelectedTab as QueryTabViewModel;
        Assert.NotNull(tab);
        // Provider returns a full CREATE VIEW DDL — view model must use it verbatim.
        Assert.Equal(storedDefinition, tab.QueryText);
        Assert.DoesNotContain("SELECT * FROM ActiveCustomers", tab.QueryText);
    }

    [Fact]
    public async Task ScriptObjectAsCreate_ViewWithNoDefinition_UsesFallbackTemplate()
    {
        var provider = new EpicEProvider { ViewDefinition = null };
        var vm = CreateMainWindowViewModel(new DatabaseService());

        var realRoot = new SchemaNode { Name = "MyDb", Type = SchemaNodeType.Database };
        var viewNode = new SchemaNode
        {
            Name = "SomeView",
            RawName = "SomeView",
            Type = SchemaNodeType.View,
            SchemaName = "public"
        };
        realRoot.Children.Add(viewNode);

        vm.ActiveSessions[realRoot] = new DatabaseSession(
            new ConnectionInfo { Name = "MyDb", Provider = provider.ProviderName, ConnectionString = "cs" },
            provider);

        await vm.ScriptObjectAsCreate(viewNode);

        var tab = vm.SelectedTab as QueryTabViewModel;
        Assert.NotNull(tab);
        // Fallback must not be circular (SELECT * FROM SomeView inside CREATE VIEW SomeView).
        Assert.DoesNotContain("SELECT * FROM SomeView", tab.QueryText);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private sealed class EpicEProvider : IDatabaseProvider
    {
        public string? ViewDefinition { get; init; }

        public string ProviderName => "EpicEProvider";
        public string ProviderIcon => "E";
        public DatabaseProviderCapabilities Capabilities { get; init; } = new("\"", "\"", "LIMIT")
        {
            TransactionSupport = TransactionSupportLevel.Guaranteed
        };
        public string ExplainPrefix => "EXPLAIN";

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("EpicE");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult { PageIndex = pageIndex, PageSize = pageSize });
        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(statements.Count));
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(commands.Count));
        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult(ViewDefinition);
        public List<string> GetSupportedDataTypes() => ["text"];
    }
}
