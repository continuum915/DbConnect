using Avalonia.Headless.XUnit;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// Issue 16 on SQLite and DuckDB: one representative table goes through Script as CREATE and recreation in
/// a separate database (compared by the engine's own catalog), then through one designer save that edits
/// columns, keys, foreign keys, checks and indexes together where the engine allows it. The reopened table
/// differs only by those edits, the seeded rows, dependent view, trigger and neighbouring tables survive,
/// the engine enforces the saved definition, the saved table scripts and recreates faithfully again, explorer
/// and completion show the new column, a stale designer is refused, a failing rebuild leaves everything as
/// it was, and saving again does nothing.
/// </summary>
public sealed class CombinedTableWorkflowTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-combined-" + Guid.NewGuid().ToString("N"));

    public CombinedTableWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private const string SqlitePrerequisites = """
        CREATE TABLE "parent.t" (a INTEGER NOT NULL, b INTEGER NOT NULL, label TEXT, PRIMARY KEY (a, b));
        CREATE TABLE audit (item_id INTEGER, action TEXT);
        """;

    // The table holds a literal dot, an AUTOINCREMENT key, modifiers, collations, a stored generated column,
    // an ordered composite unique key and foreign key, column and table checks, a partial and an expression
    // index and a trigger. A view depends on it and a table already has the rebuild's temporary name.
    private const string SqliteFixture = SqlitePrerequisites + """
        CREATE TABLE "Order.Items" (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            pa INTEGER,
            pb INTEGER,
            code VARCHAR(20) COLLATE NOCASE,
            qty DECIMAL(10,2) NOT NULL DEFAULT 1 CHECK (qty > 0),
            price REAL NOT NULL DEFAULT 0,
            total REAL GENERATED ALWAYS AS (qty * price) STORED,
            note TEXT,
            CONSTRAINT uq_code UNIQUE (pb, code),
            CONSTRAINT fk_parent FOREIGN KEY (pa, pb) REFERENCES "parent.t" (a, b) ON DELETE CASCADE,
            CONSTRAINT ck_note CHECK (note IS NULL OR length(note) < 50)
        );
        CREATE INDEX "ix.partial" ON "Order.Items" (qty DESC) WHERE note IS NOT NULL;
        CREATE INDEX ix_expr ON "Order.Items" (lower(code), price);
        CREATE TRIGGER trg_ins AFTER INSERT ON "Order.Items" BEGIN INSERT INTO audit VALUES (new.id, 'insert'); END;
        CREATE VIEW items_v AS SELECT id, code, total FROM "Order.Items";
        CREATE TABLE "_rain_tmp_Order.Items" (keep TEXT);
        INSERT INTO "_rain_tmp_Order.Items" VALUES ('untouched');
        INSERT INTO "parent.t" VALUES (1, 10, 'p1'), (2, 20, 'p2');
        INSERT INTO "Order.Items" (pa, pb, code, qty, price, note) VALUES (1, 10, 'A', 2, 1.5, 'n1'), (2, 20, 'B', 3, 2, NULL);
        """;

    private string Sqlite(string name, string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, name + ".db"), Pooling = false, ForeignKeys = true }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    /// <summary>The engine's own record of the table and everything it owns.</summary>
    private static async Task<string[]> SqliteCatalog(IDatabaseProvider provider, string cs) =>
        await CombinedWorkflow.RowsAsync(provider, cs,
            "SELECT type, name, sql FROM sqlite_master WHERE tbl_name = 'Order.Items' ORDER BY type, name;");

    [AvaloniaFact]
    public async Task Sqlite_ScriptRecreateAndCombinedDesignerEditKeepEverythingElse()
    {
        var provider = new SqliteProvider();
        var cs = Sqlite("source", SqliteFixture);
        var session = CombinedWorkflow.Session(provider, cs);

        // Script as CREATE recreates the table exactly in a database holding only the declared prerequisites.
        var script = await CombinedWorkflow.ScriptAsync(session, "main", "Order.Items");
        Assert.Contains("Referenced table \"parent.t\"", script);
        var copy = Sqlite("copy", SqlitePrerequisites);
        await provider.ExecuteNonQueryAsync(copy, script);
        Assert.Equal(await SqliteCatalog(provider, cs), await SqliteCatalog(provider, copy));

        var vm = CreateMainWindowViewModel();
        var designer = await CombinedWorkflow.OpenAsync(vm, session, null, "Order.Items");
        var root = vm.SchemaNodes.Single();
        var stale = await CombinedWorkflow.OpenAsync(CreateMainWindowViewModel(), CombinedWorkflow.Session(provider, cs), null, "Order.Items");
        var before = designer.Design.Baseline!;
        // The engine's own text for objects no edit touches, compared again after the rebuild.
        const string untouchedSql = "SELECT name, sql FROM sqlite_master WHERE name IN ('trg_ins', 'ix_expr', 'items_v') ORDER BY name;";
        var untouched = await CombinedWorkflow.RowsAsync(provider, cs, untouchedSql);
        Assert.Equal(3, untouched.Length);

        // Invalid edits are listed and block Save before anything runs.
        designer.Column("qty").Precision = 0;
        designer.Checks.Items.Single(c => c.Name == "ck_note").Expression = "note <> ''; DROP TABLE audit";
        Assert.NotEmpty(designer.ValidationErrors);
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("", designer.PreviewSql);

        // One save: precision and collation, a new generated column, a new ordered unique key, a changed
        // foreign-key action, a changed check, and a changed partial-index filter. SQLite rebuilds the table.
        designer.Column("qty").Precision = 12;
        designer.Column("code").Collation = "RTRIM";
        designer.Columns.Add(new ColumnDefinition { Name = "doubled", DataType = "REAL", IsNew = true, GeneratedExpression = "qty * 2" });
        designer.AddUniqueKey("uq_note", "note", "pa");
        designer.ForeignKeys.Items.Single(k => k.Name == "fk_parent").OnDelete = "SET NULL";
        designer.Checks.Items.Single(c => c.Name == "ck_note").Expression = "note IS NULL OR length(note) < 60";
        designer.Index("ix.partial").Filter = "note IS NOT NULL AND qty > 1";
        var reviewed = await CombinedWorkflow.SaveAsync(designer);
        Assert.Contains("\"_rain_tmp_Order.Items_", reviewed);

        var after = await ((ITableDesignSource)provider).GetTableDesignAsync(cs, designer.Design.Table!);
        CombinedWorkflow.AssertChanged(before, after,
            [
                "column code VARCHAR(20) collate=NOCASE",
                "column qty DECIMAL(10,2) NOT NULL default=1",
                "foreign key fk_parent (pa -> a, pb -> b) references main.parent.t on delete CASCADE on update NO ACTION",
                "check ck_note note IS NULL OR length(note) < 50",
                "index ix.partial CreateIndex (qty DESC) where=note IS NOT NULL"
            ],
            [
                "column code VARCHAR(20) collate=RTRIM",
                "column qty DECIMAL(12,2) NOT NULL default=1",
                "column doubled REAL generated=AS (qty * 2) VIRTUAL",
                "unique uq_note (note, pa)",
                "foreign key fk_parent (pa -> a, pb -> b) references main.parent.t on delete SET NULL on update NO ACTION",
                "check ck_note note IS NULL OR length(note) < 60",
                "index ix.partial CreateIndex (qty DESC) where=note IS NOT NULL AND qty > 1",
                "index sqlite_autoindex_Order.Items_2 unique UniqueKey (note, pa)"
            ]);
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));

        Assert.Equal(untouched, await CombinedWorkflow.RowsAsync(provider, cs, untouchedSql));

        // Untouched data, the dependent view, the trigger and the table that had the temporary name survive.
        Assert.Equal(["1|A|3|n1", "2|B|6|<null>"],
            await CombinedWorkflow.RowsAsync(provider, cs, "SELECT v.id, v.code, v.total, i.note FROM items_v v JOIN \"Order.Items\" i USING (id) ORDER BY id;"));
        Assert.Equal(["untouched"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT keep FROM \"_rain_tmp_Order.Items\";"));
        Assert.Equal(["1"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT count(*) FROM sqlite_master WHERE name = 'trg_ins';"));

        // The engine enforces the saved definition.
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO \"Order.Items\" (pa, pb, code, qty, note) VALUES (1, 10, 'c', 4, 'n3');");
        Assert.Equal(["1|4|insert", "2|6|insert", "3|8|insert"], await CombinedWorkflow.RowsAsync(provider, cs,
            "SELECT i.id, i.doubled, a.action FROM \"Order.Items\" i JOIN audit a ON a.item_id = i.id;"));
        await CombinedWorkflow.RefusedAsync(provider, cs, "INSERT INTO \"Order.Items\" (pa, pb, note) VALUES (1, 10, 'n1');", "UNIQUE constraint failed: Order.Items.note, Order.Items.pa");
        await CombinedWorkflow.RefusedAsync(provider, cs, $"INSERT INTO \"Order.Items\" (note) VALUES ('{new string('x', 60)}');", "ck_note");
        await provider.ExecuteNonQueryAsync(cs, $"INSERT INTO \"Order.Items\" (note) VALUES ('{new string('x', 55)}');");
        await provider.ExecuteNonQueryAsync(cs, "PRAGMA foreign_keys = ON; DELETE FROM \"parent.t\" WHERE a = 2;");
        Assert.Equal(["2|<null>|<null>"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT id, pa, pb FROM \"Order.Items\" WHERE id = 2;"));

        // The saved table scripts and recreates faithfully too.
        var again = Sqlite("copy2", SqlitePrerequisites);
        await provider.ExecuteNonQueryAsync(again, await CombinedWorkflow.ScriptAsync(session, "main", "Order.Items"));
        Assert.Equal(await SqliteCatalog(provider, cs), await SqliteCatalog(provider, again));

        // Explorer and completion show the new column.
        Assert.Contains("doubled", await CombinedWorkflow.ExplorerColumnsAsync(vm, root, null, "Order.Items"));
        Assert.Contains("doubled", await CombinedWorkflow.CompleteColumnsAsync(session, "\"Order.Items\""));

        // A designer opened before the save is now stale and refused.
        await CombinedWorkflow.AssertStaleAsync(stale, d => d.Column("note").Collation = "NOCASE",
            "was: column qty DECIMAL(10,2) NOT NULL default=1");

        // A rebuild whose new check the rows violate rolls back: the catalog and rows are exactly as before.
        var catalog = await SqliteCatalog(provider, cs);
        var rows = await CombinedWorkflow.RowsAsync(provider, cs, "SELECT * FROM \"Order.Items\" ORDER BY id;");
        designer.Column("price").Collation = "NOCASE";
        designer.Checks.Items.Single(c => c.Name == "ck_note").Expression = "note IS NOT NULL";
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", designer.StatusMessage);
        Assert.Contains(designer.Review.Steps, s => s.Status is "Failed" or "Rolled back");
        Assert.Equal(catalog, await SqliteCatalog(provider, cs));
        Assert.Equal(rows, await CombinedWorkflow.RowsAsync(provider, cs, "SELECT * FROM \"Order.Items\" ORDER BY id;"));
        Assert.Equal(["untouched"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT keep FROM \"_rain_tmp_Order.Items\";"));
        Assert.Equal(4, (await CombinedWorkflow.RowsAsync(provider, cs, "SELECT id FROM items_v;")).Length);

        var reopened = await CombinedWorkflow.OpenAgainAsync(vm, root, null, "Order.Items");
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(reopened.Design.Baseline!));
        await CombinedWorkflow.AssertNoOpAsync(reopened);
    }

    /// <summary>Columns are dropped before new ones are added, so one save can replace a column under the same name.</summary>
    [AvaloniaFact]
    public async Task DuckDb_ADroppedColumnsNameCanBeReusedInTheSameSave()
    {
        var provider = new DuckDbProvider();
        var cs = DuckDb("reuse", "CREATE TABLE t (id INTEGER PRIMARY KEY, note INTEGER); INSERT INTO t VALUES (1, 5);");
        var vm = CreateMainWindowViewModel();
        var designer = await CombinedWorkflow.OpenAsync(vm, CombinedWorkflow.Session(provider, cs), "main", "t");
        designer.Column("note").IsDropped = true;
        designer.Columns.Add(new ColumnDefinition { Name = "note", DataType = "VARCHAR", IsNew = true });
        Assert.Empty(designer.ValidationErrors);
        await CombinedWorkflow.SaveAsync(designer);
        Assert.Equal(["id INTEGER", "note VARCHAR"], designer.Design.Baseline!.Columns.Select(c => $"{c.Name} {c.DataType}"));
        Assert.Equal(["1|<null>"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT id, note FROM t;"));
    }

    private const string DuckDbPrerequisites = """
        CREATE SCHEMA "s.x";
        CREATE TABLE "s.x"."Parent.T" (a INTEGER NOT NULL, b INTEGER NOT NULL, PRIMARY KEY (a, b));
        """;

    // A same-named table sits in main. DuckDB keeps no constraint names, actions or triggers, and cannot
    // change constraints after creation, so the designer edits columns, comments and indexes here.
    private const string DuckDbFixture = DuckDbPrerequisites + """
        CREATE SEQUENCE "s.x".item_seq START 100;
        CREATE TABLE "s.x"."Order.Items" (
            id INTEGER DEFAULT nextval('"s.x".item_seq') PRIMARY KEY,
            pa INTEGER,
            pb INTEGER,
            code VARCHAR COLLATE NOCASE,
            qty DECIMAL(10,2) NOT NULL DEFAULT 1 CHECK (qty > 0),
            total DECIMAL(12,2) GENERATED ALWAYS AS (qty * 2) VIRTUAL,
            price DECIMAL(8,2),
            note VARCHAR,
            UNIQUE (pb, code),
            FOREIGN KEY (pa, pb) REFERENCES "s.x"."Parent.T" (a, b)
        );
        CREATE INDEX "ix.expr" ON "s.x"."Order.Items" (lower(code), qty);
        COMMENT ON TABLE "s.x"."Order.Items" IS 'it''s the items';
        COMMENT ON COLUMN "s.x"."Order.Items".qty IS 'how many';
        CREATE TABLE main."Order.Items" (elsewhere INTEGER);
        INSERT INTO main."Order.Items" VALUES (7);
        CREATE TABLE main."Parent.T" (a INTEGER NOT NULL, b INTEGER NOT NULL, PRIMARY KEY (a, b));
        INSERT INTO "s.x"."Parent.T" VALUES (1, 10), (2, 20);
        INSERT INTO "s.x"."Order.Items" (pa, pb, code, qty, note) VALUES (1, 10, 'A', 2, 'n1'), (2, 20, 'B', 3, NULL);
        """;

    private string DuckDb(string name, string sql)
    {
        var cs = $"Data Source={Path.Combine(_directory, name + ".duckdb")}";
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    private static async Task<string[]> DuckDbCatalog(IDatabaseProvider provider, string cs) =>
    [
        .. await CombinedWorkflow.RowsAsync(provider, cs, "SELECT sql, comment FROM duckdb_tables() WHERE schema_name = 's.x' AND table_name = 'Order.Items';"),
        .. await CombinedWorkflow.RowsAsync(provider, cs, "SELECT index_name, is_unique, expressions, sql, comment FROM duckdb_indexes() WHERE schema_name = 's.x' AND table_name = 'Order.Items' ORDER BY index_name;"),
        .. await CombinedWorkflow.RowsAsync(provider, cs, "SELECT column_name, data_type, column_default, is_nullable, comment FROM duckdb_columns() WHERE schema_name = 's.x' AND table_name = 'Order.Items' ORDER BY column_index;"),
        .. await CombinedWorkflow.RowsAsync(provider, cs, "SELECT constraint_type, constraint_text FROM duckdb_constraints() WHERE schema_name = 's.x' AND table_name = 'Order.Items' ORDER BY constraint_type, constraint_text;")
    ];

    [AvaloniaFact]
    public async Task DuckDb_ScriptRecreateAndCombinedDesignerEditKeepEverythingElse()
    {
        var provider = new DuckDbProvider();
        var cs = DuckDb("source", DuckDbFixture);
        var session = CombinedWorkflow.Session(provider, cs);

        var script = await CombinedWorkflow.ScriptAsync(session, "s.x", "Order.Items");
        Assert.DoesNotContain("elsewhere", script);
        var copy = DuckDb("copy", DuckDbPrerequisites + "CREATE SEQUENCE \"s.x\".item_seq START 100;");
        await provider.ExecuteNonQueryAsync(copy, script);
        Assert.Equal(await DuckDbCatalog(provider, cs), await DuckDbCatalog(provider, copy));

        // The stored foreign key names "s.x"."Parent.T"; the design reader keeps the target in the table's own
        // schema, which is safe only because DuckDB refuses a foreign key into another schema.
        await CombinedWorkflow.RefusedAsync(provider, cs,
            "CREATE TABLE \"s.x\".cross_fk (a INTEGER, b INTEGER, FOREIGN KEY (a, b) REFERENCES main.\"Parent.T\" (a, b));", "schema");

        var vm = CreateMainWindowViewModel();
        var designer = await CombinedWorkflow.OpenAsync(vm, session, "s.x", "Order.Items");
        var root = vm.SchemaNodes.Single();
        var before = designer.Design.Baseline!;

        // Changing a constraint, or the type of a column a generated column reads, is refused with DuckDB's
        // limit before anything runs.
        designer.Checks.Items.Single().Expression = "qty > 1";
        Assert.NotEmpty(designer.Plan.Blocked);
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        designer.Column("qty").Precision = 14;
        Assert.Contains("Column qty: DuckDB cannot change the type or collation of a column that generated column total reads.", designer.Plan.Blocked);
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", designer.StatusMessage);
        Assert.Equal(TableDesignText.Lines(before), TableDesignText.Lines(
            await ((ITableDesignSource)provider).GetTableDesignAsync(cs, designer.Design.Table!)));
        await designer.ReloadCommand.ExecuteAsync(null);

        // One save: precision, collation, a new column, and a changed expression index (under a new name).
        designer.Column("price").Precision = 14;
        designer.Column("note").Collation = "NOCASE";
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "VARCHAR", IsNew = true });
        var index = designer.Index("ix.expr");
        index.Name = "ix.expr2";
        index.Parts[0].Expression = "upper(code)";
        await CombinedWorkflow.SaveAsync(designer);

        var after = await ((ITableDesignSource)provider).GetTableDesignAsync(cs, designer.Design.Table!);
        CombinedWorkflow.AssertChanged(before, after,
            [
                "column price DECIMAL(8,2)",
                "column note VARCHAR",
                "index ix.expr CreateIndex (((lower(code))), qty)"
            ],
            [
                "column price DECIMAL(14,2)",
                "column note VARCHAR collate=NOCASE",
                "column added VARCHAR",
                "index ix.expr2 CreateIndex (((upper(code))), qty)"
            ]);

        // Rows, the sequence default, the generated value, keys and the neighbouring same-named table survive.
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO \"s.x\".\"Order.Items\" (pa, pb, code, qty, note) VALUES (1, 10, 'c', 4, 'N1');");
        Assert.Equal(["100|A|4|n1", "101|B|6|<null>", "102|c|8|N1"],
            await CombinedWorkflow.RowsAsync(provider, cs, "SELECT id, code, total, note FROM \"s.x\".\"Order.Items\" ORDER BY id;"));
        await CombinedWorkflow.RefusedAsync(provider, cs, "INSERT INTO \"s.x\".\"Order.Items\" (pa, pb, code) VALUES (1, 10, 'A');", "duplicate key");
        await CombinedWorkflow.RefusedAsync(provider, cs, "INSERT INTO \"s.x\".\"Order.Items\" (pa, pb) VALUES (9, 9);", "foreign key");
        await CombinedWorkflow.RefusedAsync(provider, cs, "INSERT INTO \"s.x\".\"Order.Items\" (qty) VALUES (0);", "CHECK constraint");
        Assert.Equal(["7"], await CombinedWorkflow.RowsAsync(provider, cs, "SELECT elsewhere FROM main.\"Order.Items\";"));

        var again = DuckDb("copy2", DuckDbPrerequisites + "CREATE SEQUENCE \"s.x\".item_seq START 100;");
        await provider.ExecuteNonQueryAsync(again, await CombinedWorkflow.ScriptAsync(session, "s.x", "Order.Items"));
        Assert.Equal(await DuckDbCatalog(provider, cs), await DuckDbCatalog(provider, again));

        Assert.Contains("added", await CombinedWorkflow.ExplorerColumnsAsync(vm, root, "s.x", "Order.Items"));
        Assert.Contains("added", await CombinedWorkflow.CompleteColumnsAsync(session, "\"s.x\".\"Order.Items\""));
        Assert.DoesNotContain("added", await CombinedWorkflow.CompleteColumnsAsync(session, "main.\"Order.Items\""));

        var reopened = await CombinedWorkflow.OpenAgainAsync(vm, root, "s.x", "Order.Items");
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(reopened.Design.Baseline!));
        await CombinedWorkflow.AssertNoOpAsync(reopened);
    }
}
