using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Transfers between SQLite and DuckDB, and the DDL preview (data-transfer/06).</summary>
public sealed class DataTransferAcrossEnginesTests : DumpTestDatabases
{
    private const string SqliteSource = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, balance NUMERIC(10,2), born DATE, seen DATETIME,
            active BOOLEAN DEFAULT 1, photo BLOB, note TEXT DEFAULT 'none', CHECK (length(name) > 0));
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
            total REAL, placed DATETIME, doubled INTEGER GENERATED ALWAYS AS (id * 2) VIRTUAL);
        CREATE INDEX orders_placed ON orders(placed DESC);
        INSERT INTO customers VALUES (1, 'Ann', 10.5, '1990-01-02', '2026-01-02 03:04:05', 1, x'00FF', 'x'),
            (2, 'it''s Bo', NULL, NULL, NULL, 0, NULL, 'none');
        INSERT INTO orders (id, customer_id, total, placed) VALUES (10, 1, 5.5, '2026-01-31 12:00:00'), (11, 2, 7, NULL);
        """;

    private const string DuckDbSource = """
        CREATE TYPE mood AS ENUM ('calm', 'busy');
        CREATE TABLE events (id BIGINT PRIMARY KEY, uid UUID, at_ts TIMESTAMPTZ, span INTERVAL, tags INTEGER[], amount DECIMAL(12, 3),
            big HUGEINT, ok BOOLEAN, feeling mood, day DATE, at_time TIME, payload JSON);
        INSERT INTO events VALUES
            (1, 'f3a9c1e2-0b1d-4c5e-9f00-123456789abc', TIMESTAMPTZ '2026-01-02 03:04:05.5+00', INTERVAL 90 MINUTE, [1, 2, 3], 12.345,
             123456789012345678901234567, true, 'busy', DATE '2000-02-29', TIME '23:59:59', '{"a": [1, "x"]}'),
            (2, NULL, NULL, NULL, [], NULL, NULL, false, NULL, NULL, NULL, NULL);
        """;

    [Fact]
    public async Task SqliteTablesArriveInDuckDbWithTypesKeysIndexesAndValues()
    {
        var source = await SessionAsync("SQLite", "source", SqliteSource);
        var target = await SessionAsync("DuckDB", "target", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, [Table("orders"), Table("customers")], new DataTransferOptions());

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Diagnostic}"));
        Assert.Equal("""
            balance|DECIMAL(10,2)
            born|DATE
            seen|TIMESTAMP
            active|BOOLEAN
            photo|BLOB
            """.ReplaceLineEndings("\n"), await QueryAsync(target,
            "SELECT column_name, data_type FROM duckdb_columns() WHERE table_name = 'customers' AND column_name IN ('balance', 'born', 'seen', 'active', 'photo') ORDER BY column_index"));
        Assert.Equal("1|Ann|10.50|1990-01-02|2026-01-02 03:04:05|true|\\x00\\xFF|x\n2|it's Bo|∅|∅|∅|false|∅|none", await QueryAsync(target,
            "SELECT id, name, balance::VARCHAR, born::VARCHAR, seen::VARCHAR, active::VARCHAR, photo::VARCHAR, note FROM customers ORDER BY id"));
        // The generated column's values arrive as a plain column; the foreign key and both indexes are there.
        Assert.Equal("10|1|5.5|2026-01-31 12:00:00|20\n11|2|7.0|∅|22", await QueryAsync(target,
            "SELECT id, customer_id, total::VARCHAR, placed::VARCHAR, doubled FROM orders ORDER BY id"));
        Assert.Equal("FOREIGN KEY|orders", await QueryAsync(target,
            "SELECT constraint_type, table_name FROM duckdb_constraints() WHERE constraint_type = 'FOREIGN KEY'"));
        Assert.Equal("customers|True\norders|False", await QueryAsync(target, "SELECT table_name, is_unique FROM duckdb_indexes() ORDER BY 1"));
        await using (var db = await SessionSetup.OpenAsync(target))
            Assert.True((await db.ExecuteQueryAsync("INSERT INTO customers (id, name) VALUES (3, 'Ann')")).HasError, "The unique key holds.");
        var notes = string.Join("\n", outcomes.SelectMany(o => o.Notes));
        Assert.Contains("is not carried over", notes); // the CHECK
        Assert.Contains("Generated from", notes);
        Assert.Contains("DuckDB does not support ON DELETE CASCADE", notes);
    }

    [Fact]
    public async Task DuckDbValuesArriveInSqliteAsItStoresThem()
    {
        var source = await SessionAsync("DuckDB", "source", DuckDbSource);
        var target = await SessionAsync("SQLite", "target", null);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Table("events")], new DataTransferOptions()));

        Assert.True(outcome.Status == DataTransferTableStatus.Created, outcome.Diagnostic);
        Assert.Equal(2, outcome.RowsCopied);
        Assert.Equal(
            "1|f3a9c1e2-0b1d-4c5e-9f00-123456789abc|2026-01-02 03:04:05.5+00:00|01:30:00|[1,2,3]|12.345|1|busy|2000-02-29|23:59:59|{\"a\": [1, \"x\"]}\n" +
            "2|∅|∅|∅|[]|∅|0|∅|∅|∅|∅",
            await QueryAsync(target, "SELECT id, uid, at_ts, span, tags, amount, ok, feeling, day, at_time, payload FROM events ORDER BY id"));
        // HUGEINT holds 27 digits, beyond SQLite's 8-byte numbers, so it is noted and stored as SQLite can.
        Assert.Contains(outcome.Notes, n => n.Contains("HUGEINT"));
        Assert.Contains(outcome.Notes, n => n.Contains("feeling") && n.Contains("not enforced"));
    }

    [Fact]
    public async Task IntoSqliteKeysAndIndexesNameTheirTablesWithoutASchema()
    {
        // SQLite refuses a schema in REFERENCES and on the table of CREATE INDEX.
        var source = await SessionAsync("DuckDB", "source", """
            CREATE TABLE parent (id INTEGER PRIMARY KEY, code VARCHAR UNIQUE);
            CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id), label VARCHAR);
            CREATE INDEX child_label ON child(label);
            INSERT INTO parent VALUES (1, 'a'); INSERT INTO child VALUES (1, 1, 'x');
            """);
        var target = await SessionAsync("SQLite", "target", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, [Table("child"), Table("parent")], new DataTransferOptions());

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created && !o.Notes.Any(n => n.Contains("was not created")),
            $"{o.Table.Name}: {o.Diagnostic} {string.Join("; ", o.Notes)}"));
        Assert.Equal("child|parent|parent_id|id", await QueryAsync(target,
            "SELECT 'child', \"table\", \"from\", \"to\" FROM pragma_foreign_key_list('child')"));
        Assert.Equal("child_label\nuq_parent_1", await QueryAsync(target,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name"));
    }

    [Theory]
    [InlineData(DataTransferTransactionMode.PerTable)]
    [InlineData(DataTransferTransactionMode.PerBatch)]
    public async Task AValueTheTargetCannotHoldFailsOrSkipsItsRowNamingTheColumn(DataTransferTransactionMode mode)
    {
        // SQLite keeps text in an INTEGER column; DuckDB's INTEGER cannot hold it.
        var source = await SessionAsync("SQLite", "source", "CREATE TABLE t (id INTEGER PRIMARY KEY, qty INTEGER); INSERT INTO t VALUES (1, 5), (2, 'many'), (3, 7);");
        var target = await SessionAsync("DuckDB", "target", null);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Table("t")],
            new DataTransferOptions { Transaction = mode, ContinueOnError = true }));

        if (mode == DataTransferTransactionMode.PerTable)
        {
            Assert.Equal(DataTransferTableStatus.Failed, outcome.Status);
            Assert.Contains("Row 2 was rejected: Column qty:", outcome.Diagnostic);
            Assert.Equal("", await QueryAsync(target, "SELECT table_name FROM duckdb_tables()"));
            return;
        }
        Assert.Equal(DataTransferTableStatus.Created, outcome.Status);
        var skipped = Assert.Single(outcome.Skipped);
        Assert.Equal(2, skipped.RowNumber);
        Assert.Equal("id=2", skipped.Key);
        Assert.StartsWith("Column qty:", skipped.Error);
        Assert.Equal("1|5\n3|7", await QueryAsync(target, "SELECT * FROM t ORDER BY id"));
    }

    [Fact]
    public async Task ThePreviewShowsTheScriptAndWhatDoesNotCarryOverWithoutChangingTheTarget()
    {
        var source = await SessionAsync("SQLite", "source", SqliteSource);
        var target = await SessionAsync("DuckDB", "target", null);

        var preview = await DataTransferJob.PreviewAsync(source, target, [Table("orders"), Table("customers"), Table("missing")],
            new DataTransferOptions { Mode = DataTransferTargetMode.DropAndRecreate });

        // Referenced tables first, each with its script, indexes and notes; the missing table is named with its reason.
        Assert.True(preview.IndexOf("main.customers → \"main\".\"customers\"", StringComparison.Ordinal) <
                    preview.IndexOf("main.orders → \"main\".\"orders\"", StringComparison.Ordinal), preview);
        Assert.Contains("The table is dropped if it exists, then created.", preview);
        Assert.Contains("CREATE TABLE \"main\".\"orders\" (", preview);
        Assert.Contains("FOREIGN KEY (\"customer_id\") REFERENCES \"customers\" (\"id\")", preview);
        Assert.Contains("CREATE INDEX \"orders_placed\" ON \"main\".\"orders\" (\"placed\" DESC);", preview);
        Assert.Contains("-- Not carried over:", preview);
        Assert.Contains("main.missing cannot be transferred:", preview);
        Assert.Equal("", await QueryAsync(target, "SELECT table_name FROM duckdb_tables()"));
    }
}
