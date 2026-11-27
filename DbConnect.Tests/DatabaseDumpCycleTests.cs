using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Dumps of foreign-key cycles and self-references (sql-dump-and-insert-export/06).</summary>
public sealed class DatabaseDumpCycleTests : DumpTestDatabases
{
    // a_people and b_teams reference each other; c_nodes references itself, and its first row by key references a
    // later one; d_tasks hangs off the cycle. Loading in key order with checks on fails at every one of them.
    private const string SqliteFixture = """
        PRAGMA foreign_keys=OFF;
        CREATE TABLE b_teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL, captain_id INTEGER REFERENCES a_people(id));
        CREATE TABLE a_people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, team_id INTEGER REFERENCES b_teams(id));
        CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id), label TEXT);
        CREATE TABLE d_tasks (id INTEGER PRIMARY KEY, team_id INTEGER NOT NULL REFERENCES b_teams(id), what TEXT);
        INSERT INTO b_teams VALUES (1, 'red', 2), (2, 'blue', NULL);
        INSERT INTO a_people VALUES (1, 'Ann', 2), (2, 'Bo', 1);
        INSERT INTO c_nodes VALUES (1, 3, 'leaf'), (2, NULL, 'root'), (3, 2, 'branch');
        INSERT INTO d_tasks VALUES (1, 1, 'kick off');
        PRAGMA foreign_keys=ON;
        """;

    // DuckDB cannot create a two-table cycle (no forward references, no ALTER TABLE ADD FOREIGN KEY), so its fixture
    // has the self-reference and a table hanging off it; rows reference earlier rows, as DuckDB requires to insert them.
    private const string DuckDbFixture = """
        CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id), label VARCHAR);
        CREATE TABLE b_tags (id INTEGER PRIMARY KEY, node_id INTEGER REFERENCES c_nodes(id));
        INSERT INTO c_nodes VALUES (1, NULL, 'root');
        INSERT INTO c_nodes VALUES (2, 1, 'branch');
        INSERT INTO c_nodes VALUES (3, 2, 'leaf');
        INSERT INTO b_tags VALUES (1, 3);
        """;


    public static TheoryData<string, DumpMode> Modes => new()
    {
        { "SQLite", DumpMode.StructureAndData },
        { "SQLite", DumpMode.StructureOnly },
        { "DuckDB", DumpMode.StructureAndData },
        { "DuckDB", DumpMode.StructureOnly }
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ACycleAndASelfReferenceDumpAndLoadIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        var (fixture, tables) = provider == "SQLite"
            ? (SqliteFixture, new[] { "a_people", "b_teams", "c_nodes", "d_tasks" })
            : (DuckDbFixture, new[] { "c_nodes", "b_tags" });
        var source = await SessionAsync(provider, "source", fixture);
        var path = Path.Combine(DumpDirectory, "dump.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        await using var target = await SessionSetup.OpenAsync(await SessionAsync(provider, "target", null), CancellationToken.None);
        await SqlInsertRoundTrip.ExecuteAsync(target, script);
        foreach (var table in tables)
        {
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(Session(provider, "target"), table));
            if (mode == DumpMode.StructureAndData)
                Assert.Equal(await RowsAsync(source, table), await RowsAsync(Session(provider, "target"), table));
        }
        if (provider == "SQLite")
        {
            // The script turned foreign-key checks back on for the session that ran it.
            var checks = await SqlInsertRoundTrip.QueryAsync(target, "PRAGMA foreign_keys");
            Assert.Equal(1L, Convert.ToInt64(checks.ObjectRows[0][0]));
        }
    }

    [Theory]
    [InlineData(DumpMode.StructureAndData)]
    [InlineData(DumpMode.StructureOnly)]
    public async Task SqliteHeaderNamesTheCyclesAndTheScriptTurnsChecksOffAroundTheLoad(DumpMode mode)
    {
        var source = await SessionAsync("SQLite", "header", SqliteFixture);
        var path = Path.Combine(DumpDirectory, "header.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        var header = script[..script.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("""
            -- Foreign-key cycles:
            --   main.a_people, main.b_teams
            --   main.c_nodes (references itself)
            -- Foreign-key checks: off while this script loads (PRAGMA foreign_keys=OFF;), restored at its end (PRAGMA foreign_keys=ON;); run it outside a transaction, inside which SQLite ignores the PRAGMA
            """.ReplaceLineEndings("\n"), header);
        var off = script.IndexOf("\nPRAGMA foreign_keys=OFF;\n", StringComparison.Ordinal);
        Assert.True(off > header.Length && off < script.IndexOf("\n-- Table ", StringComparison.Ordinal), script);
        Assert.EndsWith("\nPRAGMA foreign_keys=ON;\n", script);
        // Tables in a cycle come before the tables that reference them.
        Assert.True(script.IndexOf("\n-- Table main.b_teams\n", StringComparison.Ordinal) < script.IndexOf("\n-- Table main.d_tasks\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADumpWithoutCyclesLeavesForeignKeyChecksAlone()
    {
        var source = await SessionAsync("SQLite", "plain", "CREATE TABLE p (id INTEGER PRIMARY KEY); CREATE TABLE c (id INTEGER PRIMARY KEY, p_id INTEGER REFERENCES p(id));");
        var path = Path.Combine(DumpDirectory, "plain.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("foreign_keys", script);
        Assert.DoesNotContain("Foreign-key", script);
    }

    [Fact]
    public async Task DuckDbHeaderSaysSelfReferencingRowsLoadInTheOrderWritten()
    {
        var source = await SessionAsync("DuckDB", "duck-header", DuckDbFixture);
        var path = Path.Combine(DumpDirectory, "duck-header.sql");

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        var script = await File.ReadAllTextAsync(path);
        var header = script[..script.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("""
            -- Foreign-key cycles:
            --   main.c_nodes (references itself)
            -- Foreign-key checks: DuckDB cannot turn them off; every row of a self-referencing table is written after the row it references
            """.ReplaceLineEndings("\n"), header);
    }

    [Fact]
    public async Task DuckDbSelfReferencingRowsThatReferenceLaterRowsFailTheDumpNamingTheTable()
    {
        var source = await SessionAsync("DuckDB", "duck-later", """
            CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id));
            INSERT INTO c_nodes VALUES (5, NULL);
            INSERT INTO c_nodes VALUES (4, 5);
            """);
        var path = Path.Combine(DumpDirectory, "out", "later.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("Table main.c_nodes references itself, and DuckDB cannot turn off foreign-key checks: 1 row references a row " +
                     "the dump writes after it, so the script would not load. Destination was not published.", outcome.Diagnostic);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));

        // Structure alone loads whatever the rows are.
        outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureOnly, path);
        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
    }
}
