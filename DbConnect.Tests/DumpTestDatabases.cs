using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// File-backed SQLite and DuckDB databases for the dump tests, in a directory each test owns and deletes, with the
/// reads the tests compare: table snapshots, rows as the driver reads them, and query results.
/// </summary>
public abstract class DumpTestDatabases : IDisposable
{
    protected string DumpDirectory { get; } = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}");

    protected DumpTestDatabases() => Directory.CreateDirectory(DumpDirectory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DumpDirectory, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A session on the database file <paramref name="name"/>; the same name opens the same file.</summary>
    protected DatabaseSession Session(string provider, string name) => new DatabaseService().CreateSession(new ConnectionInfo
    {
        Name = name,
        Provider = provider,
        ConnectionString = $"Data Source={Path.Combine(DumpDirectory, name + (provider == "DuckDB" ? ".duckdb" : ".sqlite"))}"
    });

    protected async Task<DatabaseSession> SessionAsync(string provider, string name, string? setup)
    {
        var session = Session(provider, name);
        if (setup != null) await ExecuteScriptAsync(session, setup);
        return session;
    }

    protected static async Task ExecuteScriptAsync(DatabaseSession session, string script)
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        await SqlInsertRoundTrip.ExecuteAsync(db, script);
    }

    protected static DatabaseObjectIdentity Table(string name) => new(null, "main", name, SchemaNodeType.Table);

    protected static async Task<string> SnapshotAsync(DatabaseSession session, string table) =>
        (await ((ITableScriptSource)session.Provider).GetTableSnapshotAsync(session.ConnectionString, Table(table))).ToScript();

    /// <summary>The table's rows as the driver reads them, with their types, in a stable order: the checksum a dump must preserve.</summary>
    protected static async Task<string> RowsAsync(DatabaseSession session, string table, string orderBy = "1, 2")
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        var result = await SqlInsertRoundTrip.QueryAsync(db, $"SELECT * FROM {table} ORDER BY {orderBy}");
        Assert.NotEmpty(result.ObjectRows);
        return string.Join("\n", result.ObjectRows.Select(row => string.Join("|", row.Select(v => v is null or DBNull ? "∅" : $"{v.GetType().Name}:{v}"))));
    }

    /// <summary>A query's rows as text, for comparing two databases.</summary>
    protected static async Task<string> QueryAsync(DatabaseSession session, string sql)
    {
        await using var db = await SessionSetup.OpenAsync(session, CancellationToken.None);
        var result = await SqlInsertRoundTrip.QueryAsync(db, sql);
        return string.Join("\n", result.ObjectRows.Select(row => string.Join("|", row.Select(v => v is null or DBNull ? "∅" : v.ToString()))));
    }

    /// <summary>Every table, view, macro and trigger with its stored definition, as the engine's own catalog lists them.</summary>
    protected static Task<string> CatalogAsync(DatabaseSession session) => QueryAsync(session, session.ProviderName == "SQLite"
        ? "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name"
        : """
          SELECT 'table', schema_name, table_name, sql FROM duckdb_tables()
          UNION ALL SELECT 'view', schema_name, view_name, sql FROM duckdb_views() WHERE NOT internal
          UNION ALL SELECT function_type, schema_name, function_name || parameters::VARCHAR, macro_definition FROM duckdb_functions()
              WHERE NOT internal AND function_type IN ('macro', 'table_macro')
          ORDER BY 1, 2, 3
          """);
}
