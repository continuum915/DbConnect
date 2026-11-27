using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>A server from the container lane that creates scratch databases and drops them afterwards.</summary>
internal sealed class ScratchServer(string provider, string adminConnectionString) : IAsyncDisposable
{
    private readonly List<string> _databases = [];

    public static ScratchServer? For(string provider)
    {
        var cs = provider == "MySQL"
            ? Environment.GetEnvironmentVariable("DBCONNECT_CONFORMANCE_MYSQL_ADMIN_CONNECTION_STRING")
            : ProviderConformanceFixture.GetEnvironmentConnectionString(provider);
        return string.IsNullOrWhiteSpace(cs) ? null : new ScratchServer(provider, cs);
    }

    public string ConnectionFor(string connectionString, string database) =>
        new DbConnectionStringBuilder { ConnectionString = connectionString, ["Database"] = database }.ConnectionString;

    public async Task<DatabaseSession> CreateDatabaseAsync(string suffix, string? setup)
    {
        var name = $"rain_scratch_{suffix}_{Guid.NewGuid().ToString("N")[..8]}";
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE {name}");
        _databases.Add(name);
        var session = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = name,
            Provider = provider,
            ConnectionString = ConnectionFor(adminConnectionString, name)
        });
        if (setup != null) await RunScriptAsync(session, setup);
        return session;
    }

    public async Task RunScriptAsync(DatabaseSession session, string script)
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        // As Run submits a script: GO batches on SQL Server, statements elsewhere, honouring MySQL's DELIMITER.
        var spans = SqlBatchParser.GetExecutionSpans(script, session.Capabilities.BatchDialect);
        foreach (var batch in spans.Select(span => script.Substring(span.Start, span.Length)))
        {
            // A table's data is one SQL Server batch (12,000 INSERTs for the keyed table), which can outlast the
            // 30-second default when the whole suite shares the container.
            var result = await db.ExecuteQueryAsync(batch, new QueryExecutionOptions { TimeoutSeconds = 300 });
            Assert.False(result.HasError, $"{result.ErrorMessage}\n{(batch.Length > 2000 ? batch[..2000] + " …" : batch)}");
        }
    }

    public async Task ExecuteAsync(string connectionString, string sql)
    {
        await using DbConnection connection = provider switch
        {
            "PostgreSQL" => new NpgsqlConnection(connectionString),
            "MySQL" => new MySqlConnection(connectionString),
            _ => new SqlConnection(connectionString)
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
        MySqlConnection.ClearAllPools();
        foreach (var name in _databases)
        {
            try
            {
                await ExecuteAsync(adminConnectionString, provider switch
                {
                    "PostgreSQL" => $"DROP DATABASE IF EXISTS {name} WITH (FORCE)",
                    "MySQL" => $"DROP DATABASE IF EXISTS {name}",
                    _ => $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END"
                });
            }
            catch (DbException) { /* A scratch database left behind does not affect other runs. */ }
        }
    }
}

/// <summary>Reads that compare two scratch databases: table definitions and rows, with the database name left out.</summary>
internal static class ScratchComparisons
{
    public static string Database(DatabaseSession session) =>
        new DbConnectionStringBuilder { ConnectionString = session.ConnectionString }["Database"]!.ToString()!;

    public static DatabaseObjectIdentity Identity(DatabaseSession session, string table) =>
        session.ProviderName == "MySQL"
            ? new(Database(session), null, table, SchemaNodeType.Table)
            : new(Database(session), session.Capabilities.DefaultSchema, table, SchemaNodeType.Table);

    /// <summary>The table's definition statements; the database name is left out since the two databases differ.</summary>
    public static async Task<string> StatementsAsync(DatabaseSession session, string table) =>
        (await ((ITableScriptSource)session.Provider).GetTableSnapshotAsync(session.ConnectionString, Identity(session, table)))
        .OrderedStatements().Replace(Database(session), "<db>");

    /// <summary>The rows as the driver reads them, in a stable order; SQL Server rowversion values differ per database, so callers leave them out.</summary>
    public static async Task<string> RowsAsync(DatabaseSession session, string table, string columns = "*")
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        var result = await db.ExecuteQueryAsync($"SELECT {columns} FROM {session.Capabilities.QuoteIdentifier(table)} ORDER BY 1, 2");
        Assert.False(result.HasError, result.ErrorMessage);
        Assert.NotEmpty(result.ObjectRows);
        return string.Join("\n", result.ObjectRows.Select(row => string.Join("|", row.Select(v => v switch
        {
            null or DBNull => "∅",
            byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
            _ => $"{v.GetType().Name}:{v}"
        }))));
    }
}
