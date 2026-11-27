using System;
using System.Threading;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>milestone-0 ticket 02 — Test and Connect share one path through the connection establisher.</summary>
public class ConnectionEstablisherTests : MainWindowTestBase
{
    [Fact]
    public async Task Test_ReturnsTheProvidersResultFromAnotherThread()
    {
        var provider = new FakeProvider();
        var establisher = CreateEstablisher(provider);
        var callingThread = Environment.CurrentManagedThreadId;

        var result = await establisher.TestAsync(Connection());

        Assert.True(result.Success);
        Assert.Equal("Fake 1.0", result.ServerVersion);
        Assert.Equal(["Data Source=fake"], provider.TestedConnectionStrings);
        Assert.NotEqual(callingThread, provider.TestThread);
    }

    [Fact]
    public async Task Establish_SessionAndSchemaUseTheEffectiveSettings()
    {
        var provider = new FakeProvider { Schema = [new SchemaNode { Name = "main" }] };
        var establisher = CreateEstablisher(provider);
        var saved = Connection();
        var callingThread = Environment.CurrentManagedThreadId;

        using var established = await establisher.EstablishAsync(saved);

        Assert.NotSame(saved, established.Settings);
        Assert.Equal("Fake Connection", established.Settings.Name);
        Assert.Same(established.Settings, established.Session.Connection);
        Assert.Same(provider, established.Session.Provider);
        Assert.Equal("main", Assert.Single(established.Schema).Name);
        Assert.Equal(["Data Source=fake"], provider.SchemaConnectionStrings);
        Assert.NotEqual(callingThread, provider.SchemaThread);
    }

    [Fact]
    public async Task Test_CancelledWhileTheProviderIsTesting_Throws()
    {
        var provider = new FakeProvider { Gate = new TaskCompletionSource() };
        var establisher = CreateEstablisher(provider);
        using var cancellation = new CancellationTokenSource();

        var test = establisher.TestAsync(Connection(), cancellation.Token);
        await provider.Entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.WaitAsync(TimeSpan.FromSeconds(5)));
        provider.Gate.SetResult();
    }

    [Fact]
    public async Task Establish_CancelledWhileLoadingSchema_HandsBackNothing()
    {
        var provider = new FakeProvider { Gate = new TaskCompletionSource() };
        var establisher = CreateEstablisher(provider);
        using var cancellation = new CancellationTokenSource();

        var establish = establisher.EstablishAsync(Connection(), cancellation.Token);
        await provider.Entered.Task;
        cancellation.Cancel();
        provider.Gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establish.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Establish_SchemaLoadFails_ReportsSafeMetadataFailure()
    {
        var provider = new FakeProvider { SchemaError = new InvalidOperationException("schema unavailable") };
        var establisher = CreateEstablisher(provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => establisher.EstablishAsync(Connection()));

        Assert.Equal("Metadata could not be loaded. Retry to try again.", error.Message);
    }

    [Fact]
    public async Task ConnectionDialogTest_GoesThroughTheEstablisherOffTheCallingThread()
    {
        var provider = new FakeProvider("SQLite");
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        var dialog = new ConnectionDialogViewModel(databases, new ConnectionSettingsService())
        {
            SelectedProvider = "SQLite",
            DatabaseFilePath = "fake.db"
        };
        var callingThread = Environment.CurrentManagedThreadId;

        await dialog.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(dialog.TestResult?.Success);
        Assert.Equal(["Data Source=fake.db"], provider.TestedConnectionStrings);
        Assert.NotEqual(callingThread, provider.TestThread);
    }

    [Fact]
    public async Task ConnectionDialogTest_CancelledWhileTesting_ShowsNoResult()
    {
        var provider = new FakeProvider("SQLite") { Gate = new TaskCompletionSource() };
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        var dialog = new ConnectionDialogViewModel(databases, new ConnectionSettingsService())
        {
            SelectedProvider = "SQLite",
            DatabaseFilePath = "fake.db"
        };

        var test = dialog.TestConnectionCommand.ExecuteAsync(null);
        await provider.Entered.Task;
        dialog.TestConnectionCommand.Cancel();
        await test.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Gate.SetResult();

        Assert.Null(dialog.TestResult);
        Assert.Equal("Test cancelled.", dialog.StatusMessage);
    }

    [AvaloniaFact]
    public async Task MainWindowConnect_UsesTheEstablishedConnectionAndDisconnectDisposesIt()
    {
        var provider = new FakeProvider { Schema = [new SchemaNode { Name = "main", Children = { new SchemaNode { Name = "Customers" } } }] };
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        var vm = CreateMainWindowViewModel(databases);
        var saved = Connection();

        await vm.AddConnectionAsync(saved);

        var root = Assert.Single(vm.SchemaNodes);
        var established = vm.EstablishedConnections[root];
        Assert.Same(established.Session, vm.ActiveSessions[root]);
        Assert.Same(established.Settings, vm.ActiveConnections[root]);
        Assert.NotSame(saved, established.Settings);
        Assert.Equal("Customers", Assert.Single(root.Children).Name);

        vm.Disconnect(root);

        Assert.True(established.IsDisposed);
        Assert.Empty(vm.EstablishedConnections);
    }

    [AvaloniaFact]
    public async Task MainWindowConnect_SchemaLoadFails_AddsNothing()
    {
        var provider = new FakeProvider { SchemaError = new InvalidOperationException("schema unavailable") };
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        var vm = CreateMainWindowViewModel(databases);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.AddConnectionAsync(Connection()));

        Assert.Empty(vm.SchemaNodes);
        Assert.Empty(vm.ActiveSessions);
        Assert.Empty(vm.EstablishedConnections);
    }

    private static ConnectionEstablisher CreateEstablisher(FakeProvider provider)
    {
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        return new ConnectionEstablisher(databases);
    }

    private static ConnectionInfo Connection() => new()
    {
        Name = "Fake Connection",
        Provider = FakeProvider.Name,
        ConnectionString = "Data Source=fake"
    };

    internal sealed class FakeProvider : IDatabaseProvider
    {
        public const string Name = "Fake";

        public FakeProvider(string providerName = Name) => ProviderName = providerName;

        public List<string> TestedConnectionStrings { get; } = [];
        public int TestThread { get; private set; }
        public ConnectionTestResult TestResult { get; init; } = ConnectionTestResult.Succeeded("Fake 1.0", TimeSpan.FromMilliseconds(5));
        public List<SchemaNode> Schema { get; init; } = [];
        public Exception? SchemaError { get; init; }
        /// <summary>When set, test and schema calls wait on it after signalling <see cref="Entered"/>.</summary>
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> SchemaConnectionStrings { get; } = [];
        public int SchemaThread { get; private set; }

        public string ProviderName { get; }
        public string ProviderIcon => "F";
        public DatabaseProviderCapabilities Capabilities { get; } = new("\"", "\"", "LIMIT");
        public string ExplainPrefix => "EXPLAIN";

        public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString)
        {
            TestThread = Environment.CurrentManagedThreadId;
            TestedConnectionStrings.Add(connectionString);
            await PassGateAsync();
            return TestResult;
        }

        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("Fake 1.0");
        public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
        {
            SchemaThread = Environment.CurrentManagedThreadId;
            SchemaConnectionStrings.Add(connectionString);
            await PassGateAsync();
            return SchemaError is null ? Schema : throw SchemaError;
        }

        private async Task PassGateAsync()
        {
            Entered.TrySetResult();
            if (Gate is not null)
                await Gate.Task;
        }

        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());
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
