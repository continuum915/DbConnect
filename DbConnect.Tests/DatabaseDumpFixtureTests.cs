using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// The spec's dump fixture on SQLite and DuckDB (sql-dump-and-insert-export/09): one database with a foreign-key chain,
/// a cycle, a self-reference, views, a routine and a trigger, where the engine has them, dumped in both modes, loaded
/// into an empty database and compared by catalog, table snapshot and rows.
/// </summary>
public sealed class DatabaseDumpFixtureTests : DumpTestDatabases
{
    // z_customers ← m_orders ← a_lines is a chain in reverse alphabetical order; a_people and b_teams reference each
    // other; c_nodes references itself with its first row by key pointing at a later one; a_summary reads z_order_totals;
    // lines_audit writes audit for every line inserted.
    private const string SqliteFixture = """
        PRAGMA foreign_keys=OFF;
        CREATE TABLE z_customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL UNIQUE, note VARCHAR);
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES z_customers(id), placed DATE, total DECIMAL(10,2));
        CREATE TABLE a_lines (order_id INTEGER REFERENCES m_orders(id), line INTEGER, qty INTEGER CHECK (qty > 0),
            doubled INTEGER GENERATED ALWAYS AS (qty * 2) VIRTUAL, PRIMARY KEY (order_id, line));
        CREATE TABLE b_teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL, captain_id INTEGER REFERENCES a_people(id));
        CREATE TABLE a_people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, team_id INTEGER REFERENCES b_teams(id));
        CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id), label TEXT);
        CREATE TABLE audit (what TEXT);
        CREATE VIEW z_order_totals AS SELECT o.id, sum(l.doubled) AS doubled FROM m_orders o JOIN a_lines l ON l.order_id = o.id GROUP BY o.id;
        CREATE VIEW a_summary AS SELECT count(*) AS n, sum(doubled) AS total FROM z_order_totals;
        CREATE TRIGGER lines_audit AFTER INSERT ON a_lines BEGIN INSERT INTO audit VALUES ('line ' || NEW.order_id || '/' || NEW.line); END;
        INSERT INTO z_customers VALUES (1, 'O''Brien', 'line one
        line two'), (2, 'héllo 日本語', NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31', 12.50), (11, 2, NULL, NULL), (12, 1, '2024-02-29', 0.01);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (12, 1, 7);
        INSERT INTO b_teams VALUES (1, 'red', 2), (2, 'blue', NULL);
        INSERT INTO a_people VALUES (1, 'Ann', 2), (2, 'Bo', 1);
        INSERT INTO c_nodes VALUES (1, 3, 'leaf'), (2, NULL, 'root'), (3, 2, 'branch');
        PRAGMA foreign_keys=ON;
        """;

    // DuckDB has no triggers and cannot create a two-table cycle (no forward references, no ALTER TABLE ADD FOREIGN
    // KEY), and its self-referencing rows must reference earlier ones; doubled is a macro the view calls.
    private const string DuckDbFixture = """
        CREATE TABLE z_customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL UNIQUE, note VARCHAR);
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES z_customers(id), placed DATE, total DECIMAL(10,2));
        CREATE TABLE a_lines (order_id INTEGER REFERENCES m_orders(id), line INTEGER, qty INTEGER CHECK (qty > 0),
            doubled INTEGER GENERATED ALWAYS AS (qty * 2) VIRTUAL, PRIMARY KEY (order_id, line));
        CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id), label VARCHAR);
        CREATE MACRO tripled(n) AS n * 3;
        CREATE VIEW z_order_totals AS SELECT o.id, sum(tripled(l.qty)) AS tripled FROM m_orders o JOIN a_lines l ON l.order_id = o.id GROUP BY o.id;
        CREATE VIEW a_summary AS SELECT count(*) AS n, sum(tripled) AS total FROM z_order_totals;
        INSERT INTO z_customers VALUES (1, 'O''Brien', 'line one
        line two'), (2, 'héllo 日本語', NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31', 12.50), (11, 2, NULL, NULL), (12, 1, '2024-02-29', 0.01);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (12, 1, 7);
        INSERT INTO c_nodes VALUES (1, NULL, 'root');
        INSERT INTO c_nodes VALUES (2, 1, 'branch');
        INSERT INTO c_nodes VALUES (3, 2, 'leaf');
        """;

    private static readonly string[] SqliteTables = ["z_customers", "m_orders", "a_lines", "b_teams", "a_people", "c_nodes", "audit"];
    private static readonly string[] DuckDbTables = ["z_customers", "m_orders", "a_lines", "c_nodes"];

    public static TheoryData<string, DumpMode> Cases => new()
    {
        { "SQLite", DumpMode.StructureAndData }, { "SQLite", DumpMode.StructureOnly },
        { "DuckDB", DumpMode.StructureAndData }, { "DuckDB", DumpMode.StructureOnly }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheSpecFixtureDumpsAndLoadsIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        var sqlite = provider == "SQLite";
        var source = await SessionAsync(provider, "source", sqlite ? SqliteFixture : DuckDbFixture);
        var path = Path.Combine(DumpDirectory, "dump.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        Assert.Equal(mode == DumpMode.StructureAndData ? (sqlite ? 18 : 11) : 0, outcome.RowsWritten);
        var target = await SessionAsync(provider, "target", null);
        await ExecuteScriptAsync(target, await File.ReadAllTextAsync(path));

        // DuckDB stores a table's text as written, and the dump's CREATE TABLE names referenced tables with their
        // schema, so its tables are compared by snapshot only.
        Assert.Equal(await ObjectCatalogAsync(source), await ObjectCatalogAsync(target));
        foreach (var table in sqlite ? SqliteTables : DuckDbTables)
        {
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(target, table));
            if (mode == DumpMode.StructureAndData) Assert.Equal(await RowsAsync(source, table, table == "audit" ? "1" : "1, 2"), await RowsAsync(target, table, table == "audit" ? "1" : "1, 2"));
            else Assert.Equal("", await QueryAsync(target, $"SELECT * FROM {table}"));
        }
        if (mode == DumpMode.StructureOnly) return;
        Assert.Equal(await QueryAsync(source, "SELECT * FROM a_summary"), await QueryAsync(target, "SELECT * FROM a_summary"));
        if (sqlite) Assert.Equal("", await QueryAsync(target, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task ALineBreakInsideATableDefinitionsLiteralKeepsItsCarriageReturn()
    {
        var source = await SessionAsync("SQLite", "crlf", "CREATE TABLE t (id INTEGER PRIMARY KEY, note TEXT DEFAULT 'a\r\nb' CHECK (note <> 'x\r\ny'));");
        var path = Path.Combine(DumpDirectory, "crlf.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureOnly, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var target = await SessionAsync("SQLite", "crlf-target", null);
        await ExecuteScriptAsync(target, await File.ReadAllTextAsync(path));
        await ExecuteScriptAsync(target, "INSERT INTO t (id) VALUES (1);");
        Assert.Equal("a\r\nb", await QueryAsync(target, "SELECT note FROM t"));
        Assert.Equal(await CatalogAsync(source), await CatalogAsync(target));
    }

    private static async Task<string> ObjectCatalogAsync(DatabaseSession session) => session.ProviderName == "SQLite"
        ? await CatalogAsync(session)
        : string.Join("\n", (await CatalogAsync(session)).Split('\n').Where(row => !row.StartsWith("table|", StringComparison.Ordinal)));
}
