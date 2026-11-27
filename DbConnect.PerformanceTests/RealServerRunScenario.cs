using Microsoft.Data.SqlClient;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

/// <summary>
/// Runs the production executor against a real SQL Server, in a throwaway database, and checks the rows
/// it stored. Opt-in: it needs the same connection variable as the provider conformance tests.
/// </summary>
public sealed class RealServerRunScenario : IAsyncDisposable
{
    public const string Id = "execute.sqlserver";
    public const string ManualCommitId = "execute.sqlserver-manual";
    public const string ConnectionVariable = "DBCONNECT_CONFORMANCE_SQLSERVER_CONNECTION_STRING";
    private const string DatabasePrefix = "rain_perf_";
    // The tab's default, which now applies to a whole batch rather than one statement.
    private const int CommandTimeoutSeconds = 30;

    private readonly SqlServerProvider _provider = new();
    private readonly ImportFixture _fixture;
    private readonly bool _manualCommit;
    private readonly string _serverConnection;
    private readonly string _connection;
    private readonly string _database = DatabasePrefix + Guid.NewGuid().ToString("N");
    private IDbSession? _session;
    private QueryBatchExecutor.BatchExecutionResult? _result;

    public static bool IsRealServer(string id) => id is Id or ManualCommitId;

    public RealServerRunScenario(string id, ImportFixture fixture)
    {
        _fixture = fixture;
        _manualCommit = id == ManualCommitId;
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException($"{id} requires {ConnectionVariable}.");
        _serverConnection = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString;
        _connection = new SqlConnectionStringBuilder(configured) { InitialCatalog = _database }.ConnectionString;
    }

    public async Task PrepareAsync()
    {
        // A timed-out child is killed before it can drop its database.
        var stale = await _provider.ExecuteQueryAsync(_serverConnection,
            $"SELECT name FROM sys.databases WHERE name LIKE '{DatabasePrefix}%'");
        foreach (var row in stale.ObjectRows)
            await DropAsync((string)row[0]!);
        await _provider.ExecuteNonQueryAsync(_serverConnection, $"CREATE DATABASE [{_database}]; ALTER DATABASE [{_database}] SET RECOVERY SIMPLE;");
        await _provider.ExecuteNonQueryAsync(_connection,
            "CREATE TABLE Parents (Id int PRIMARY KEY, Name nvarchar(max) NOT NULL); " +
            "CREATE TABLE Children (Id int PRIMARY KEY, ParentId int NOT NULL REFERENCES Parents (Id), Note nvarchar(max) NOT NULL);");
        _session = await _provider.OpenSessionAsync(new ConnectionInfo { Provider = _provider.ProviderName, ConnectionString = _connection });
    }

    public async Task ExecuteAsync()
    {
        var session = _session ?? throw new InvalidOperationException("Prepare first.");
        if (_manualCommit) await session.BeginTransactionAsync();
        _result = await QueryBatchExecutor.ExecuteAsync(
            new DatabaseSession(new ConnectionInfo { Provider = _provider.ProviderName, ConnectionString = _connection }, _provider),
            session, _fixture.Sql, QueryExecutionScope.FullEditor, CommandTimeoutSeconds, 100, _ => Task.CompletedTask, default);
        if (_manualCommit) await session.CommitAsync();
    }

    public async Task<ExecutionObservations> ValidateAsync()
    {
        var result = _result ?? throw new InvalidOperationException("Execute first.");
        if (result.DisplayResult.HasError)
            throw new InvalidDataException($"The script failed: {result.DisplayResult.ErrorMessage}");
        await CheckTableAsync("Parents", "Name", "O''Brien ");
        await CheckTableAsync("Children", "Note", "line one");
        return new(0, result.DisplayResult.StatementCount, _fixture.Sql.Length, 0, _fixture.Expected.Count, result.Results.Count, 0, 0, 0,
            [$"Real SQL Server, no database-side throttling. {result.DisplayResult.StatementCount} commands for {_fixture.Metadata.Inserts} INSERTs and {_fixture.Metadata.GoDirectives} GO batches."]);
    }

    private async Task CheckTableAsync(string table, string textColumn, string expectedPrefix)
    {
        var expected = _fixture.Expected.Where(insert => insert.Table == table).ToArray();
        var check = await _provider.ExecuteQueryAsync(_connection,
            $"SELECT COUNT_BIG(*), SUM(CAST(Id AS bigint)), SUM(CASE WHEN {textColumn} LIKE N'{expectedPrefix}%' THEN 1 ELSE 0 END) FROM {table}");
        var row = check.ObjectRows.Single();
        if (Convert.ToInt64(row[0]) != expected.Length || Convert.ToInt64(row[1]) != expected.Sum(insert => (long)insert.Id) ||
            Convert.ToInt64(row[2]) != expected.Length)
            throw new InvalidDataException($"{table} does not contain the rows the script inserts.");
    }

    private async Task DropAsync(string database)
    {
        if (database.StartsWith(DatabasePrefix, StringComparison.Ordinal))
            await _provider.ExecuteNonQueryAsync(_serverConnection,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    public async ValueTask DisposeAsync()
    {
        if (_session != null) await _session.DisposeAsync();
        SqlConnection.ClearAllPools();
        await DropAsync(_database);
    }
}
