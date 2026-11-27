using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;

using static DbConnect.Tests.ProviderConformance.ScratchComparisons;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Dump SQL File ▸ Structure and Data on PostgreSQL, MySQL and SQL Server (sql-dump-and-insert-export/05): a fresh
/// database with a foreign-key chain, a generated column and common types is dumped, loaded into another empty
/// database, and compared by table snapshot and row checksums. Skipped unless the container lane's
/// DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables are set (build/databases/run-provider-integration.ps1).
/// </summary>
public sealed class DatabaseDumpServerTests
{
    private static readonly string[] Tables = ["z_customers", "m_orders", "a_lines"];

    private const string PostgreSqlFixture = """
        CREATE TABLE z_customers (id integer PRIMARY KEY, name varchar(50) NOT NULL UNIQUE, note text, born date,
            balance numeric(10,2), active boolean, payload jsonb, data bytea, seen timestamptz);
        CREATE TABLE m_orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES z_customers(id), placed timestamp);
        CREATE TABLE a_lines (order_id integer REFERENCES m_orders(id), line integer, qty integer CHECK (qty > 0),
            doubled integer GENERATED ALWAYS AS (qty * 2) STORED, PRIMARY KEY (order_id, line));
        INSERT INTO z_customers VALUES (1, 'O''Brien', E'line one\nline two \\ back', '2000-02-29', 12.50, true, '{"a": [1, "x"]}', '\xDEADBEEF', '2026-01-02 03:04:05.123456+02'),
            (2, 'héllo 日本語', NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (11, 1, 7);
        CREATE TABLE k_events (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, s serial, what text);
        INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
        DELETE FROM k_events WHERE what = 'two';
        """;

    private const string MySqlFixture = """
        CREATE TABLE z_customers (id int PRIMARY KEY, name varchar(50) NOT NULL UNIQUE, note text, born date,
            balance decimal(10,2), active boolean, payload json, data varbinary(16), seen datetime(6)) ENGINE=InnoDB;
        CREATE TABLE m_orders (id int PRIMARY KEY, customer_id int NOT NULL, placed datetime(3),
            CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES z_customers(id)) ENGINE=InnoDB;
        CREATE TABLE a_lines (order_id int, line int, qty int CHECK (qty > 0), doubled int GENERATED ALWAYS AS (qty * 2) STORED,
            PRIMARY KEY (order_id, line), CONSTRAINT fk_lines_order FOREIGN KEY (order_id) REFERENCES m_orders(id)) ENGINE=InnoDB;
        INSERT INTO z_customers VALUES (1, 'O''Brien', 'line one\nline two \\ back', '2000-02-29', 12.50, true, '{"a": [1, "x"]}', 0xDEADBEEF, '2026-01-02 03:04:05.123456'),
            (2, 'héllo 日本語', NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (11, 1, 7);
        CREATE TABLE k_events (id int AUTO_INCREMENT PRIMARY KEY, what varchar(20)) ENGINE=InnoDB;
        INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
        DELETE FROM k_events WHERE what = 'two';
        """;

    private const string SqlServerFixture = """
        CREATE TABLE z_customers (id int PRIMARY KEY, name nvarchar(50) NOT NULL UNIQUE, note nvarchar(max), born date,
            balance decimal(10,2), active bit, data varbinary(16), seen datetimeoffset(7), uid uniqueidentifier);
        CREATE TABLE m_orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES z_customers(id), placed datetime2(3));
        CREATE TABLE a_lines (order_id int REFERENCES m_orders(id), line int, qty int CHECK (qty > 0), doubled AS (qty * 2) PERSISTED,
            PRIMARY KEY (order_id, line));
        INSERT INTO z_customers VALUES (1, N'O''Brien', N'line one' + CHAR(10) + N'line two', '2000-02-29', 12.50, 1, 0xDEADBEEF,
            '2026-01-02 03:04:05.1234567 +02:00', 'F3A9C1E2-0B1D-4C5E-9F00-123456789ABC'),
            (2, N'héllo 日本語', NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL);
        INSERT INTO a_lines (order_id, line, qty) VALUES (10, 1, 3), (10, 2, 1), (11, 1, 7);
        CREATE TABLE k_events (id int IDENTITY(1,1) PRIMARY KEY, what nvarchar(20), rv rowversion);
        INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
        DELETE FROM k_events WHERE what = 'two';
        """;

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task StructureAndDataDumpLoadsIntoAnEmptyDatabase(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("src", provider switch
        {
            "PostgreSQL" => PostgreSqlFixture,
            "MySQL" => MySqlFixture,
            _ => SqlServerFixture
        });
        var target = await server.CreateDatabaseAsync("dst", null);
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            Assert.Equal(9, outcome.RowsWritten);
            var script = await File.ReadAllTextAsync(path);
            await server.RunScriptAsync(target, script);
            foreach (var table in Tables)
            {
                Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
                Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
            }

            // Identity and serial values load as they were, and both databases go on generating the same next values.
            Assert.Equal(await StatementsAsync(source, "k_events"), await StatementsAsync(target, "k_events"));
            foreach (var session in new[] { source, target })
                await server.RunScriptAsync(session, "INSERT INTO k_events (what) VALUES ('next')");
            var events = provider == "SQL Server" ? "id, what" : "*";
            Assert.Equal(await RowsAsync(source, "k_events", events), await RowsAsync(target, "k_events", events));
        }
        finally { File.Delete(path); }
    }

    // z_orders_view calls doubled, a_summary reads z_orders_view, and on PostgreSQL and SQL Server order_rows reads
    // z_orders_view in a body the engine checks at CREATE, so catalog order would not load (sql-dump-and-insert-export/07).
    private static string ObjectsFixture(string provider) => provider switch
    {
        "PostgreSQL" => """
            CREATE TABLE t_orders (id int PRIMARY KEY, qty int NOT NULL);
            CREATE TABLE t_audit (order_id int, what text);
            CREATE FUNCTION doubled(n int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT n * 2 $$;
            CREATE VIEW z_orders_view AS SELECT id, doubled(qty) AS d FROM t_orders;
            CREATE VIEW a_summary AS SELECT count(*) AS n, sum(d) AS total FROM z_orders_view;
            CREATE FUNCTION order_rows() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM z_orders_view $$;
            CREATE PROCEDURE add_order(i int, q int) LANGUAGE sql AS $$ INSERT INTO t_orders VALUES (i, q) $$;
            CREATE FUNCTION audit_order() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO t_audit VALUES (NEW.id, 'inserted'); RETURN NEW; END $$;
            CREATE TRIGGER orders_audit AFTER INSERT ON t_orders FOR EACH ROW EXECUTE FUNCTION audit_order();
            CREATE FUNCTION view_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO t_orders VALUES (NEW.id, NEW.d / 2); RETURN NEW; END $$;
            CREATE TRIGGER view_insert INSTEAD OF INSERT ON z_orders_view FOR EACH ROW EXECUTE FUNCTION view_insert();
            INSERT INTO t_orders VALUES (1, 3), (2, 5);
            """,
        "MySQL" => """
            CREATE TABLE t_orders (id int PRIMARY KEY, qty int NOT NULL) ENGINE=InnoDB;
            CREATE TABLE t_audit (order_id int, what varchar(50)) ENGINE=InnoDB;
            CREATE FUNCTION doubled(n int) RETURNS int DETERMINISTIC RETURN n * 2;
            CREATE VIEW z_orders_view AS SELECT id, doubled(qty) AS d FROM t_orders;
            CREATE VIEW a_summary AS SELECT count(*) AS n, sum(d) AS total FROM z_orders_view;
            DELIMITER $$
            CREATE PROCEDURE add_order(i int, q int) BEGIN INSERT INTO t_orders VALUES (i, q); END$$
            CREATE TRIGGER orders_audit AFTER INSERT ON t_orders FOR EACH ROW BEGIN INSERT INTO t_audit VALUES (NEW.id, 'inserted'); END$$
            DELIMITER ;
            INSERT INTO t_orders VALUES (1, 3), (2, 5);
            """,
        _ => """
            CREATE TABLE t_orders (id int PRIMARY KEY, qty int NOT NULL);
            CREATE TABLE t_audit (order_id int, what nvarchar(50));
            GO
            CREATE FUNCTION dbo.doubled(@n int) RETURNS int AS BEGIN RETURN @n * 2; END;
            GO
            CREATE VIEW dbo.z_orders_view AS SELECT id, dbo.doubled(qty) AS d FROM dbo.t_orders;
            GO
            CREATE VIEW dbo.a_summary AS SELECT count(*) AS n, sum(d) AS total FROM dbo.z_orders_view;
            GO
            CREATE FUNCTION dbo.order_rows() RETURNS TABLE AS RETURN SELECT id, d FROM dbo.z_orders_view;
            GO
            CREATE PROCEDURE dbo.add_order @i int, @q int AS INSERT INTO dbo.t_orders VALUES (@i, @q);
            GO
            CREATE TRIGGER dbo.orders_audit ON dbo.t_orders AFTER INSERT AS INSERT INTO dbo.t_audit SELECT id, 'inserted' FROM inserted;
            GO
            CREATE TRIGGER dbo.view_insert ON dbo.z_orders_view INSTEAD OF INSERT AS INSERT INTO dbo.t_orders SELECT id, d / 2 FROM inserted;
            GO
            INSERT INTO t_orders VALUES (1, 3), (2, 5);
            """
    };

    public static TheoryData<string, DumpMode> ObjectCases => new()
    {
        { "PostgreSQL", DumpMode.StructureAndData }, { "PostgreSQL", DumpMode.StructureOnly },
        { "MySQL", DumpMode.StructureAndData }, { "MySQL", DumpMode.StructureOnly },
        { "SQL Server", DumpMode.StructureAndData }, { "SQL Server", DumpMode.StructureOnly }
    };

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(ObjectCases))]
    public async Task ViewsRoutinesAndTriggersDumpAndLoadIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("obj", ObjectsFixture(provider));
        var target = await server.CreateDatabaseAsync("objdst", null);
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            var script = await File.ReadAllTextAsync(path);
            await server.RunScriptAsync(target, script);
            Assert.Equal(await ObjectsAsync(source), await ObjectsAsync(target));
            foreach (var table in new[] { "t_orders", "t_audit" })
                Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"TRIGGER\s+\S*orders_audit\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (provider == "PostgreSQL")
            {
                // PostgreSQL's trigger reader returns the trigger's function, so a trigger on a view is stated, not scripted.
                Assert.Contains("\n--   Trigger public.view_insert on view public.z_orders_view: its definition could not be read.\n", script);
                Assert.DoesNotContain("Trigger function", script);
            }
            if (mode == DumpMode.StructureOnly) return;
            Assert.Equal(await RowsAsync(source, "a_summary"), await RowsAsync(target, "a_summary"));
            Assert.Equal(await RowsAsync(source, "t_audit"), await RowsAsync(target, "t_audit"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The views, routines and view triggers as the dump reads them; the database name is left out.</summary>
    private static async Task<string> ObjectsAsync(DatabaseSession session)
    {
        var objects = await DumpObjects.ReadAsync(session, await DumpCatalog.ResolveAsync(session, DumpSelection.Database), CancellationToken.None);
        Assert.NotEmpty(objects.ViewsAndRoutines);
        return string.Join("\n", objects.ViewsAndRoutines.Concat(objects.Triggers)
            .Select(o => $"{o.Title} {ObjectNames.SchemaQualified(o.Identity)}: {o.Definition.Trim().Replace(Database(session), "<db>")}")
            .Order(StringComparer.Ordinal));
    }

    // a_people and b_teams reference each other, c_nodes references itself with its first row by key pointing at a
    // later one, and d_tasks hangs off the cycle (sql-dump-and-insert-export/06).
    private static string CycleFixture(string provider)
    {
        var (text, engine) = provider switch
        {
            "PostgreSQL" => ("text", ""),
            "MySQL" => ("varchar(50)", " ENGINE=InnoDB"),
            _ => ("nvarchar(50)", "")
        };
        return $"""
            CREATE TABLE b_teams (id int PRIMARY KEY, name {text} NOT NULL, captain_id int){engine};
            CREATE TABLE a_people (id int PRIMARY KEY, name {text} NOT NULL, team_id int,
                CONSTRAINT a_people_team FOREIGN KEY (team_id) REFERENCES b_teams(id)){engine};
            ALTER TABLE b_teams ADD CONSTRAINT b_teams_captain FOREIGN KEY (captain_id) REFERENCES a_people(id);
            CREATE TABLE c_nodes (id int PRIMARY KEY, parent_id int, label {text},
                CONSTRAINT c_nodes_parent FOREIGN KEY (parent_id) REFERENCES c_nodes(id)){engine};
            CREATE TABLE d_tasks (id int PRIMARY KEY, team_id int NOT NULL,
                CONSTRAINT d_tasks_team FOREIGN KEY (team_id) REFERENCES b_teams(id)){engine};
            INSERT INTO b_teams VALUES (1, 'red', NULL), (2, 'blue', NULL);
            INSERT INTO a_people VALUES (1, 'Ann', 2), (2, 'Bo', 1);
            UPDATE b_teams SET captain_id = 2 WHERE id = 1;
            INSERT INTO c_nodes VALUES (2, NULL, 'root');
            INSERT INTO c_nodes VALUES (3, 2, 'branch');
            INSERT INTO c_nodes VALUES (1, 3, 'leaf');
            INSERT INTO d_tasks VALUES (1, 1);
            {(provider == "PostgreSQL" ? "COMMENT ON CONSTRAINT b_teams_captain ON b_teams IS 'the captain';" : "")}
            """;
    }

    public static TheoryData<string, DumpMode> CycleCases => new()
    {
        { "PostgreSQL", DumpMode.StructureAndData }, { "PostgreSQL", DumpMode.StructureOnly },
        { "MySQL", DumpMode.StructureAndData }, { "MySQL", DumpMode.StructureOnly },
        { "SQL Server", DumpMode.StructureAndData }, { "SQL Server", DumpMode.StructureOnly }
    };

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(CycleCases))]
    public async Task ACycleAndASelfReferenceDumpAndLoadIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("cyc", CycleFixture(provider));
        var target = await server.CreateDatabaseAsync("cycdst", null);
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            var script = await File.ReadAllTextAsync(path);
            Assert.Contains(provider == "MySQL"
                ? "\n-- Foreign-key checks: off while this script loads (SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0;)"
                : $"\n-- Foreign keys in these cycles: added after {(mode == DumpMode.StructureAndData ? "the data" : "all tables are created")}\n", script);
            await server.RunScriptAsync(target, script);
            foreach (var table in new[] { "a_people", "b_teams", "c_nodes", "d_tasks" })
            {
                Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
                if (mode == DumpMode.StructureAndData) Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
            }
        }
        finally { File.Delete(path); }
    }

    // The spec's fixture in one database (sql-dump-and-insert-export/09): the foreign-key chain and common types, the
    // cycle and self-reference, and the views, routines and triggers. Their table names do not overlap.
    private static string SpecFixture(string provider) => string.Join(provider == "SQL Server" ? "\nGO\n" : "\n",
        provider switch { "PostgreSQL" => PostgreSqlFixture, "MySQL" => MySqlFixture, _ => SqlServerFixture },
        CycleFixture(provider), ObjectsFixture(provider));

    private static readonly string[] SpecTables =
        ["z_customers", "m_orders", "a_lines", "a_people", "b_teams", "c_nodes", "d_tasks", "t_orders", "t_audit"];

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(ObjectCases))]
    public async Task TheSpecFixtureDumpsAndLoadsIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("spec", SpecFixture(provider));
        var target = await server.CreateDatabaseAsync("specdst", null);
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, mode, path);

            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            Assert.Equal(mode == DumpMode.StructureAndData ? 21 : 0, outcome.RowsWritten);
            await server.RunScriptAsync(target, await File.ReadAllTextAsync(path));
            Assert.Equal(await ObjectsAsync(source), await ObjectsAsync(target));
            foreach (var table in SpecTables.Append("k_events"))
                Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            if (mode == DumpMode.StructureOnly) return;
            foreach (var table in SpecTables.Append("a_summary"))
                Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
            var events = provider == "SQL Server" ? "id, what" : "*";
            Assert.Equal(await RowsAsync(source, "k_events", events), await RowsAsync(target, "k_events", events));
        }
        finally { File.Delete(path); }
    }

    // More rows than a dump page (5,000), under a key of text, date and integer, so pages after the first are read
    // past the last key. SQL Server's SQL collations sort varchar 'co-op…' before 'coop…' and nvarchar after it.
    private static string KeyedFixture(string provider) => provider switch
    {
        "PostgreSQL" => """
            CREATE TABLE keyed (grp varchar(20), day date, n int, label text, PRIMARY KEY (grp, day, n));
            INSERT INTO keyed SELECT CASE WHEN i % 2 = 0 THEN 'co-op' ELSE 'coop' END || (i % 13), DATE '2026-01-01' + i % 40, i, 'row ' || i
                FROM generate_series(1, 12000) i;
            """,
        "MySQL" => """
            CREATE TABLE keyed (grp varchar(20), day date, n int, label text, PRIMARY KEY (grp, day, n)) ENGINE=InnoDB;
            SET SESSION cte_max_recursion_depth = 20000;
            INSERT INTO keyed WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 12000)
                SELECT CONCAT(CASE WHEN i % 2 = 0 THEN 'co-op' ELSE 'coop' END, i % 13), DATE '2026-01-01' + INTERVAL i % 40 DAY, i, CONCAT('row ', i) FROM s;
            """,
        _ => """
            CREATE TABLE keyed (grp varchar(20), day date, n int, label nvarchar(20), PRIMARY KEY (grp, day, n));
            WITH s AS (SELECT TOP 12000 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO keyed SELECT CASE WHEN i % 2 = 0 THEN 'co-op' ELSE 'coop' END + CAST(i % 13 AS varchar(2)),
                DATEADD(day, i % 40, '2026-01-01'), i, CONCAT('row ', i) FROM s;
            """
    };

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task ATableLargerThanAPageIsReadPastEachPagesLastKey(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("keyed", KeyedFixture(provider));
        var target = await server.CreateDatabaseAsync("keyeddst", null);
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);

            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            Assert.Equal(12000, outcome.RowsWritten);
            await server.RunScriptAsync(target, await File.ReadAllTextAsync(path));
            Assert.Equal(await RowsAsync(source, "keyed", "n, label"), await RowsAsync(target, "keyed", "n, label"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL", "-- Data: read in one snapshot (REPEATABLE READ READ ONLY)")]
    [InlineData("MySQL", "-- Data: read in one snapshot (START TRANSACTION WITH CONSISTENT SNAPSHOT; InnoDB tables only)")]
    [InlineData("SQL Server", "-- Data: not a single snapshot; each table was read as it stood when the dump reached it")]
    public async Task HeaderStatesTheSnapshotTheDataWasReadIn(string provider, string expected)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("hdr", provider == "PostgreSQL" ? "CREATE TABLE t (id integer PRIMARY KEY);"
            : "CREATE TABLE t (id int PRIMARY KEY);");

        Assert.Contains(expected + "\n", await DumpAsync(source));
        if (provider != "SQL Server") return;

        // Snapshot isolation is off by default; once the database allows it, the dump reads in it.
        await server.ExecuteAsync(server.ConnectionFor(source.Connection.ConnectionString, "master"),
            $"ALTER DATABASE [{Database(source)}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        Assert.Contains("-- Data: read in one snapshot (SNAPSHOT isolation)\n", await DumpAsync(source));
    }

    private static async Task<string> DumpAsync(DatabaseSession source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-dump-{Guid.NewGuid():N}.sql");
        try
        {
            var outcome = await DatabaseDumpService.DumpToFileAsync(source, DumpSelection.Database, DumpMode.StructureAndData, path);
            Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
            return await File.ReadAllTextAsync(path);
        }
        finally { File.Delete(path); }
    }
}
