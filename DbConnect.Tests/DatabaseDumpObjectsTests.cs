using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Views, routines and triggers in Dump SQL File (sql-dump-and-insert-export/07).</summary>
public sealed class DatabaseDumpObjectsTests : DumpTestDatabases
{
    // a_summary reads z_orders_view, so alphabetical order would create it before the view it reads.
    private const string SqliteFixture = """
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL);
        CREATE TABLE audit (what TEXT);
        CREATE VIEW z_orders_view AS SELECT id, qty * 2 AS doubled FROM m_orders;
        CREATE VIEW a_summary AS SELECT count(*) AS n, sum(doubled) AS total FROM "z_orders_view";
        CREATE TRIGGER orders_audit AFTER INSERT ON m_orders BEGIN INSERT INTO audit VALUES ('order ' || NEW.id); END;
        CREATE TRIGGER orders_view_insert INSTEAD OF INSERT ON z_orders_view
            BEGIN INSERT INTO m_orders (id, qty) VALUES (NEW.id, NEW.doubled / 2); END;
        INSERT INTO m_orders VALUES (1, 3), (2, 5);
        """;

    // a_view calls the macro and reads z_base; table_rows is a table macro; scaled has two overloads; counted reads a
    // table in a subquery, which DuckDB checks when the macro is created.
    private const string DuckDbFixture = """
        CREATE TABLE m_orders (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL);
        CREATE MACRO counted(x) AS (SELECT count(*) FROM m_orders) + x;
        CREATE MACRO scaled(a) AS a * 10, (a, b) AS a * b;
        CREATE MACRO table_rows(minimum) AS TABLE SELECT * FROM m_orders WHERE qty >= minimum;
        CREATE VIEW z_base AS SELECT id, scaled(qty) AS big FROM m_orders;
        CREATE VIEW a_view AS SELECT id, counted(big) AS c FROM z_base;
        CREATE SCHEMA reports;
        CREATE VIEW reports."Order Totals" AS SELECT sum(qty) AS total FROM main.m_orders;
        INSERT INTO m_orders VALUES (1, 3), (2, 5);
        """;


    public static TheoryData<string, DumpMode> Cases => new()
    {
        { "SQLite", DumpMode.StructureAndData }, { "SQLite", DumpMode.StructureOnly },
        { "DuckDB", DumpMode.StructureAndData }, { "DuckDB", DumpMode.StructureOnly }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ViewsRoutinesAndTriggersDumpAndLoadIntoAnEmptyDatabase(string provider, DumpMode mode)
    {
        var source = await SessionAsync(provider, "source", provider == "SQLite" ? SqliteFixture : DuckDbFixture);
        var script = await DumpAsync(source, DumpSelection.Database, mode);

        // The dump creates no schemas; it names the ones it needs.
        var target = await SessionAsync(provider, "target", provider == "DuckDB" ? "CREATE SCHEMA reports;" : null);
        await ExecuteScriptAsync(target, script);

        Assert.Equal(await CatalogAsync(source), await CatalogAsync(target));
        if (mode == DumpMode.StructureOnly) return;
        Assert.Equal(await QueryAsync(source, "SELECT * FROM m_orders ORDER BY id"), await QueryAsync(target, "SELECT * FROM m_orders ORDER BY id"));
        // The views read what was loaded.
        var summary = provider == "SQLite" ? "SELECT n, total FROM a_summary" : "SELECT * FROM a_view ORDER BY id";
        Assert.Equal(await QueryAsync(source, summary), await QueryAsync(target, summary));
    }

    [Fact]
    public async Task SqliteObjectsFollowTheTablesAndEachViewFollowsTheViewItReads()
    {
        var source = await SessionAsync("SQLite", "order", SqliteFixture);
        var script = await DumpAsync(source, DumpSelection.Database, DumpMode.StructureAndData);

        var sections = new[]
        {
            "\n-- Table main.m_orders\n", "\n-- Data main.m_orders\n", "\n-- View main.z_orders_view\n", "\n-- View main.a_summary\n",
            "\n-- Triggers main.m_orders\n", "\n-- Trigger main.orders_view_insert on main.z_orders_view\n"
        };
        var positions = sections.Select(s => script.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0, script));
        Assert.Equal(positions.Order(), positions);
        // The table's own trigger comes from its snapshot only.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "CREATE TRIGGER orders_audit"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "CREATE TRIGGER orders_view_insert"));
        Assert.Contains("-- Includes: 2 tables, triggers, 2 views, table data\n", script);
        Assert.Contains("-- Excludes: sequences, types and other objects that are not tables, views, routines or triggers; users, roles and grants\n", script);
    }

    [Fact]
    public async Task DuckDbMacrosComeBeforeTheViewsThatCallThemAndTheHeaderCountsThem()
    {
        var source = await SessionAsync("DuckDB", "macros", DuckDbFixture);
        var script = await DumpAsync(source, DumpSelection.Database, DumpMode.StructureOnly);

        var sections = new[] { "\n-- Function main.scaled\n", "\n-- View main.z_base\n", "\n-- Function main.counted\n", "\n-- View main.a_view\n" };
        var positions = sections.Select(s => script.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0, script));
        Assert.True(positions[0] < positions[1] && positions[1] < positions[3] && positions[2] < positions[3], script);
        Assert.Contains("\n-- Function main.table_rows\nCREATE OR REPLACE MACRO \"main\".\"table_rows\"(\"minimum\") AS TABLE ", script);
        Assert.Contains("-- Includes: 1 table, 3 views, 3 functions\n", script);
        Assert.Contains("-- Prerequisites (must already exist; not scripted):\n--   Schema \"reports\"\n", script);
    }

    [Fact]
    public async Task SchemaSelectionDumpsOnlyThatSchemasObjects()
    {
        var source = await SessionAsync("DuckDB", "schema", DuckDbFixture + "CREATE TABLE reports.t (id INTEGER);");
        var script = await DumpAsync(source, DumpSelection.ForSchemas("reports"), DumpMode.StructureOnly);

        Assert.Contains("\n-- View reports.Order Totals\n", script);
        Assert.DoesNotContain("z_base", script);
        Assert.DoesNotContain("MACRO", script);
    }

    [Fact]
    public async Task ATableSelectionDumpsNoViewsOrRoutines()
    {
        var source = await SessionAsync("SQLite", "tables", SqliteFixture);
        var script = await DumpAsync(source, DumpSelection.ForTables(new DatabaseObjectIdentity(null, "main", "m_orders", SchemaNodeType.Table)),
            DumpMode.StructureOnly);

        Assert.DoesNotContain("CREATE VIEW", script);
        Assert.DoesNotContain("orders_view_insert", script);
        Assert.Contains("\n-- Triggers main.m_orders\n", script);
        Assert.Contains("-- Excludes: table data; views, routines and other objects outside these tables; users, roles and grants\n", script);
    }

    [Fact]
    public async Task AnObjectWhoseDefinitionCannotBeReadIsListedAsAnExclusion()
    {
        var source = await SessionAsync("SQLite", "unread", SqliteFixture);
        var gone = new DatabaseObjectIdentity(null, "main", "gone", SchemaNodeType.View);
        // Two overloads of one name, as PostgreSQL lists them; SQLite reads no functions at all.
        var overloads = new[] { "a int", "a text" }.Select(signature =>
            new DatabaseObjectIdentity(null, "main", "stamp", SchemaNodeType.Function) { RoutineSignature = signature });
        var catalog = new DumpCatalog([], [gone, .. overloads], ["Functions in main could not be listed: denied"]);

        var objects = await DumpObjects.ReadAsync(source, catalog, CancellationToken.None);

        Assert.Empty(objects.ViewsAndRoutines);
        Assert.Equal([
            "Functions in main could not be listed: denied",
            "Function main.stamp(a int): its definition could not be read.",
            "Function main.stamp: it has 2 overloads and only one of them can be read, so the others are not scripted.",
            "View main.gone: its definition could not be read."
        ], objects.Exclusions);
        Assert.False(objects.CreatesFunction(new DatabaseObjectIdentity(null, "main", "stamp", SchemaNodeType.Function)));
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1", SchemaNodeType.View, true)]
    [InlineData("CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`%` SQL SECURITY DEFINER VIEW `v` AS select 1", SchemaNodeType.View, true)]
    [InlineData("-- note\n/* more */\ncreate   procedure dbo.p (@a int) as select 1", SchemaNodeType.Procedure, true)]
    [InlineData("CREATE DEFINER=`root`@`%` TRIGGER t BEFORE INSERT ON x FOR EACH ROW SET NEW.a = 1", SchemaNodeType.Trigger, true)]
    [InlineData("CREATE OR REPLACE FUNCTION public.stamp()\n RETURNS trigger\n LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END $f$", SchemaNodeType.Trigger, false)]
    [InlineData("CREATE OR REPLACE MACRO \"main\".\"m\"(\"a\") AS a", SchemaNodeType.Function, true)]
    [InlineData("(a + b)", SchemaNodeType.Function, false)]
    [InlineData("SELECT 1", SchemaNodeType.View, false)]
    public void OnlyAStatementThatCreatesTheObjectCountsAsItsDefinition(string definition, SchemaNodeType kind, bool expected) =>
        Assert.Equal(expected, DumpObjects.IsCreateStatement(definition, kind));

    [Fact]
    public void StatementsFollowTheTableSnapshotsScriptRules()
    {
        var procedure = new DumpObject(new(null, "dbo", "p", SchemaNodeType.Procedure), "CREATE PROCEDURE p AS BEGIN SELECT 1; END;");
        var view = new DumpObject(new(null, null, "v", SchemaNodeType.View), "CREATE VIEW v AS SELECT 1");

        var sqlServer = ProviderCapabilityDeclarations.SqlServer.ScriptRules;
        var mySql = ProviderCapabilityDeclarations.MySql.ScriptRules;
        Assert.Equal("CREATE PROCEDURE p AS BEGIN SELECT 1; END;\nGO", procedure.Statement(sqlServer));
        Assert.Equal("DELIMITER $$\nCREATE PROCEDURE p AS BEGIN SELECT 1; END$$\nDELIMITER ;", procedure.Statement(mySql));
        Assert.Equal("CREATE VIEW v AS SELECT 1;", view.Statement(mySql));
        Assert.Equal("CREATE VIEW v AS SELECT 1\nGO", view.Statement(sqlServer));
        Assert.Equal("CREATE VIEW v AS SELECT 1;", view.Statement(ProviderCapabilityDeclarations.PostgreSql.ScriptRules));
    }

    [Fact]
    public void ObjectsFollowTheObjectsTheirDefinitionsNameAndKeepTheGivenOrderOtherwise()
    {
        static DumpObject View(string name, string body) => new(new(null, "s", name, SchemaNodeType.View), $"CREATE VIEW {name} AS {body}");
        var objects = new[]
        {
            View("a", "SELECT * FROM [c] -- b\n"), View("b", "SELECT 'a', 1"), View("c", "SELECT * FROM \"D\""), View("d", "SELECT 1"),
            View("e", "SELECT * FROM f"), View("f", "SELECT * FROM e")
        };

        var order = DumpObjectOrder.Sort(objects).Select(o => o.Identity.Name);

        // a needs c, which needs d (names match without regard to case); e and f name each other and keep their order.
        Assert.Equal(["b", "d", "c", "a", "e", "f"], order);
    }

    private async Task<string> DumpAsync(DatabaseSession source, DumpSelection selection, DumpMode mode)
    {
        var path = Path.Combine(DumpDirectory, $"{Guid.NewGuid():N}.sql");
        var outcome = await DatabaseDumpService.DumpToFileAsync(source, selection, mode, path);
        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        return await File.ReadAllTextAsync(path);
    }
}
