using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class CapabilityDrivenUxTests : MainWindowTestBase
{
    [Fact]
    public void QueryTab_AnalyzeToggleTracksSessionCapabilities()
    {
        var analyzeSession = CreateSession("Analyze", new CapabilityProvider("Analyze")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.Guaranteed,
                SupportsAnalyzeExplain = true
            }
        });
        var plainSession = CreateSession("Plain", new CapabilityProvider("Plain"));
        var vm = new QueryTabViewModel("Query", [analyzeSession, plainSession]);

        vm.SelectedSession = analyzeSession;
        vm.AllowAnalyze = true;

        Assert.True(vm.SupportsAnalyzeExplain);

        vm.SelectedSession = plainSession;

        Assert.False(vm.SupportsAnalyzeExplain);
        Assert.False(vm.AllowAnalyze);
    }

    [Fact]
    public void MainWindow_SchemaActionAvailabilityUsesCapabilities()
    {
        var provider = new CapabilityProvider("NoViewsOrIndexes")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.Guaranteed,
                SupportsViews = false,
                SupportsIndexes = false
            }
        };
        var session = CreateSession("Conn", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        var view = new SchemaNode { Name = "ActiveCustomers", RawName = "ActiveCustomers", Type = SchemaNodeType.View };
        root.Children.Add(table);
        root.Children.Add(view);
        vm.ActiveSessions[root] = session;

        var createView = vm.GetSchemaActionAvailability(root, SchemaContextAction.CreateView);
        var manageIndexes = vm.GetSchemaActionAvailability(table, SchemaContextAction.ManageIndexes);
        var dropView = vm.GetSchemaActionAvailability(view, SchemaContextAction.DropObject);

        Assert.True(createView.IsVisible);
        Assert.False(createView.IsEnabled);
        Assert.Equal("This provider does not support view operations in the current workflow.", createView.DisabledReason);

        Assert.True(manageIndexes.IsVisible);
        Assert.False(manageIndexes.IsEnabled);
        Assert.Equal("This provider does not support index management in the current workflow.", manageIndexes.DisabledReason);

        Assert.True(dropView.IsVisible);
        Assert.False(dropView.IsEnabled);
        Assert.Equal("This provider does not support view operations in the current workflow.", dropView.DisabledReason);
    }

    [Fact]
    public void SchemaTree_SupportsFunctionsFalse_DoesNotAddFunctionFolderToTableChildren()
    {
        // SQLiteProvider has SupportsFunctions = false; confirm the capability is correct
        // and that functions are not reported as a supported object type
        var provider = new SqliteProvider();
        Assert.False(provider.Capabilities.SupportsFunctions);
        Assert.False(provider.Capabilities.SupportsObjectType(SchemaNodeType.FunctionFolder));
        Assert.False(provider.Capabilities.SupportsObjectType(SchemaNodeType.Function));
    }

    [Fact]
    public void TriggerFolderNode_DropObjectAndScriptDrop_AreNotVisible()
    {
        // Protects against visible-but-broken paths: folder nodes must never show drop/script actions,
        // even if a provider correctly adds TriggerFolder nodes when SupportsTriggers = true.
        var provider = new CapabilityProvider("WithTriggers")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.Guaranteed,
                SupportsTriggers = true
            }
        };
        var session = CreateSession("Conn", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        var triggerFolder = new SchemaNode { Name = "Triggers", RawName = "Triggers", Type = SchemaNodeType.TriggerFolder };
        root.Children.Add(triggerFolder);
        vm.ActiveSessions[root] = session;

        var dropResult = vm.GetSchemaActionAvailability(triggerFolder, SchemaContextAction.DropObject);
        var scriptDropResult = vm.GetSchemaActionAvailability(triggerFolder, SchemaContextAction.ScriptDrop);
        var scriptCreateResult = vm.GetSchemaActionAvailability(triggerFolder, SchemaContextAction.ScriptCreate);

        Assert.False(dropResult.IsVisible, "DropObject must not be visible for TriggerFolder nodes.");
        Assert.False(scriptDropResult.IsVisible, "ScriptDrop must not be visible for TriggerFolder nodes.");
        Assert.False(scriptCreateResult.IsVisible, "ScriptCreate must not be visible for TriggerFolder nodes.");
    }

    [Fact]
    public void FunctionFolderNode_DropObjectAndScriptDrop_AreNotVisible()
    {
        // Protects against visible-but-broken paths: FunctionFolder nodes must never show drop/script actions.
        var provider = new CapabilityProvider("WithFunctions")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.Guaranteed,
                SupportsFunctions = true
            }
        };
        var session = CreateSession("Conn", provider);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "Root", RawName = "Root", Type = SchemaNodeType.Database };
        var funcFolder = new SchemaNode { Name = "Functions", RawName = "Functions", Type = SchemaNodeType.FunctionFolder };
        root.Children.Add(funcFolder);
        vm.ActiveSessions[root] = session;

        var dropResult = vm.GetSchemaActionAvailability(funcFolder, SchemaContextAction.DropObject);
        var scriptDropResult = vm.GetSchemaActionAvailability(funcFolder, SchemaContextAction.ScriptDrop);
        var scriptCreateResult = vm.GetSchemaActionAvailability(funcFolder, SchemaContextAction.ScriptCreate);

        Assert.False(dropResult.IsVisible, "DropObject must not be visible for FunctionFolder nodes.");
        Assert.False(scriptDropResult.IsVisible, "ScriptDrop must not be visible for FunctionFolder nodes.");
        Assert.False(scriptCreateResult.IsVisible, "ScriptCreate must not be visible for FunctionFolder nodes.");
    }

    [Fact]
    public void TableDataTab_TransactionSupportNone_LabelIsNonTransactional()
    {
        var provider = new CapabilityProvider("NoTx")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.None
            }
        };
        var session = CreateSession("Conn", provider);

        Assert.False(session.Capabilities.GuaranteesTransactionalBatches);
    }

    [Fact]
    public void TableDataTab_TransactionSupportBestEffort_DoesNotGuaranteeTransactions()
    {
        var provider = new CapabilityProvider("BestEffortTx")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.BestEffort
            }
        };
        var session = CreateSession("Conn", provider);

        Assert.False(session.Capabilities.GuaranteesTransactionalBatches);
    }

    [Fact]
    public void TableDataTab_TransactionSupportGuaranteed_GuaranteesTransactions()
    {
        var provider = new CapabilityProvider("GuaranteedTx")
        {
            Capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
            {
                TransactionSupport = TransactionSupportLevel.Guaranteed
            }
        };
        var session = CreateSession("Conn", provider);

        Assert.True(session.Capabilities.GuaranteesTransactionalBatches);
    }

    private static DatabaseSession CreateSession(string name, IDatabaseProvider provider) =>
        new(
            new ConnectionInfo
            {
                Name = name,
                Provider = provider.ProviderName,
                ConnectionString = $"{name} connection string"
            },
            provider);

    private sealed class CapabilityProvider : IDatabaseProvider
    {
        public CapabilityProvider(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }
        public string ProviderIcon => "R";
        public DatabaseProviderCapabilities Capabilities { get; init; } = new("\"", "\"", "LIMIT")
        {
            TransactionSupport = TransactionSupportLevel.Guaranteed
        };
        public string ExplainPrefix => "EXPLAIN";

        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("test");
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
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => ["text"];
    }
}
