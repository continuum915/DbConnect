using Avalonia.Headless.XUnit;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Designer load and unrelated-edit round trip for SQLite and DuckDB fixtures (issue 06).</summary>
public sealed class TableDesignWorkflowTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-design-flow-" + Guid.NewGuid().ToString("N"));

    public TableDesignWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [AvaloniaTheory]
    [InlineData("Order.Items", false)]
    [InlineData("counter", false)]
    [InlineData("plain", false)]
    [InlineData("odd", true)]
    public async Task Sqlite_UnrelatedEditRoundTripKeepsTheDesignOrIsBlocked(string table, bool blocked)
    {
        var path = Path.Combine(_directory, "flow.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(cs))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = TableDesignSqliteTests.Fixture;
            await cmd.ExecuteNonQueryAsync();
        }
        var session = new DatabaseSession(new ConnectionInfo { Name = "flow", Provider = "SQLite", ConnectionString = cs }, new SqliteProvider());
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, null, table);
        var lost = await TableDesignRoundTrip.RunAsync(vm, node, session,
            new DatabaseObjectIdentity(null, "main", table, SchemaNodeType.Table), "INTEGER");
        if (blocked) Assert.Null(lost);
        else Assert.True(lost is { Count: 0 }, lost == null ? "Save was blocked." : string.Join("\n", lost));
    }

    /// <summary>The plan names the table by its schema, so the fixture saves in s.x (next to a same-named table in main) as well as in main.</summary>
    [AvaloniaTheory]
    [InlineData("s.x")]
    [InlineData("main")]
    public async Task DuckDb_UnrelatedEditRoundTripKeepsTheDesign(string schema)
    {
        var fixture = schema == "main"
            ? TableDesignDuckDbTests.Fixture.Replace("CREATE SCHEMA \"s.x\";", "").Replace("\"s.x\".", "main.")
                .Replace("CREATE TABLE main.\"Order.Items\" (elsewhere INTEGER);", "")
            : TableDesignDuckDbTests.Fixture;
        var cs = $"Data Source={Path.Combine(_directory, "flow.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = fixture;
            cmd.ExecuteNonQuery();
        }
        var session = new DatabaseSession(new ConnectionInfo { Name = "flow", Provider = "DuckDB", ConnectionString = cs }, new DuckDbProvider());
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Order.Items");
        var lost = await TableDesignRoundTrip.RunAsync(vm, node, session,
            new DatabaseObjectIdentity(null, schema, "Order.Items", SchemaNodeType.Table), "INTEGER");
        Assert.True(lost is { Count: 0 }, lost == null ? "Save was blocked." : string.Join("\n", lost));
    }
}
