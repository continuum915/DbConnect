using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Same-provider data transfer between two SQLite or two DuckDB databases (data-transfer/01).</summary>
public sealed class DataTransferJobTests : DumpTestDatabases
{
    private const string SqliteFixture = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, balance REAL, photo BLOB, joined TEXT DEFAULT CURRENT_TIMESTAMP);
        CREATE INDEX customers_name ON customers(name);
        CREATE TABLE "order items" ("order id" INTEGER NOT NULL, line INTEGER NOT NULL, "qty" INTEGER CHECK (qty > 0), PRIMARY KEY ("order id", line));
        CREATE TABLE notes (body TEXT, n INTEGER);
        INSERT INTO customers (id, name, balance, photo, joined) VALUES
            (1, 'Ann', 10.5, x'00FF10', '2026-01-02'), (2, 'it''s Bo', NULL, NULL, '2026-02-03'), (3, 'Cy', -3.25, x'', '2026-03-04'),
            (4, 'Di', 0, NULL, '2026-04-05'), (5, 'Ed', 1e10, x'AB', '2026-05-06');
        INSERT INTO "order items" VALUES (1, 1, 2), (1, 2, 1), (2, 1, 7);
        INSERT INTO notes VALUES ('same', 1), ('same', 1), (NULL, NULL);
        """;

    private const string DuckDbFixture = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, balance DECIMAL(12, 2), active BOOLEAN, born DATE,
            seen TIMESTAMP, ratio DOUBLE, photo BLOB);
        CREATE INDEX customers_name ON customers(name);
        CREATE TABLE "order items" ("order id" INTEGER NOT NULL, line INTEGER NOT NULL, qty INTEGER CHECK (qty > 0), PRIMARY KEY ("order id", line));
        CREATE TABLE notes (body VARCHAR, n BIGINT);
        INSERT INTO customers VALUES
            (1, 'Ann', 10.50, true, DATE '1990-01-02', TIMESTAMP '2026-01-02 03:04:05.123456', 0.5, '\x00\xFF'::BLOB),
            (2, 'it''s Bo', NULL, NULL, NULL, NULL, NULL, NULL),
            (3, 'Cy', -3.25, false, DATE '2000-02-29', TIMESTAMP '1999-12-31 23:59:59', -1e300, ''::BLOB),
            (4, 'Di', 0, true, DATE '2020-06-30', TIMESTAMP '2020-06-30 00:00:00', 3.0, NULL),
            (5, 'Ed', 9999999999.99, false, DATE '1970-01-01', TIMESTAMP '1970-01-01 00:00:00', 1e-10, 'x'::BLOB);
        INSERT INTO "order items" VALUES (1, 1, 2), (1, 2, 1), (2, 1, 7);
        INSERT INTO notes VALUES ('same', 1), ('same', 1), (NULL, NULL);
        """;

    private static readonly string[] Tables = ["customers", "order items", "notes"];

    private string Fixture(string provider) => provider == "SQLite" ? SqliteFixture : DuckDbFixture;

    private static IReadOnlyList<DatabaseObjectIdentity> Identities(params string[] names) => names.Select(Table).ToList();

    private static string Quoted(string name) => $"\"{name}\"";

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task CopiesEveryTableWithItsDefinitionAndRowsIntoAnEmptyDatabase(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture(provider));
        var target = await SessionAsync(provider, "target", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, Identities(Tables), new DataTransferOptions { BatchSize = 2 });

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Status} {o.Diagnostic}"));
        Assert.Equal([5L, 3L, 3L], outcomes.Select(o => o.RowsCopied));
        foreach (var table in Tables)
        {
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(target, table));
            Assert.Equal(await RowsAsync(source, Quoted(table)), await RowsAsync(target, Quoted(table)));
        }
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task AppendsToATableThatAlreadyExists(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture(provider));
        var target = await SessionAsync(provider, "target", """
            CREATE TABLE "order items" ("order id" INTEGER NOT NULL, line INTEGER NOT NULL, qty INTEGER, PRIMARY KEY ("order id", line));
            INSERT INTO "order items" VALUES (9, 9, 9);
            """);

        var outcomes = await DataTransferJob.RunAsync(source, target, Identities("order items"), new DataTransferOptions());

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DataTransferTableStatus.Appended, outcome.Status);
        Assert.Equal(3, outcome.RowsCopied);
        Assert.Equal("1|1|2\n1|2|1\n2|1|7\n9|9|9", await QueryAsync(target, """SELECT * FROM "order items" ORDER BY 1, 2"""));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task ATableThatFailsRollsBackAndTheNextTableIsStillCopied(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture(provider));
        // Row 4 of customers collides with the target's key after two batches of two rows went in.
        var target = await SessionAsync(provider, "target", provider == "SQLite"
            ? "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, balance REAL, photo BLOB, joined TEXT); INSERT INTO customers (id, name) VALUES (4, 'kept');"
            : "CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, balance DECIMAL(12, 2), active BOOLEAN, born DATE, seen TIMESTAMP, ratio DOUBLE, photo BLOB); INSERT INTO customers (id, name) VALUES (4, 'kept');");

        var outcomes = await DataTransferJob.RunAsync(source, target, Identities("customers", "notes"), new DataTransferOptions { BatchSize = 2 });

        Assert.Equal(DataTransferTableStatus.Failed, outcomes[0].Status);
        Assert.Equal(0, outcomes[0].RowsCopied);
        Assert.Contains("One of rows 3–4 was rejected", outcomes[0].Diagnostic);
        Assert.Contains("rolled back", outcomes[0].Diagnostic);
        Assert.Equal("4|kept", await QueryAsync(target, "SELECT id, name FROM customers"));
        Assert.Equal(DataTransferTableStatus.Created, outcomes[1].Status);
        Assert.Equal(await RowsAsync(source, "notes"), await RowsAsync(target, "notes"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task CancelRollsBackTheTableInProgressAndStartsNoOther(string provider)
    {
        var source = await SessionAsync(provider, "source", Fixture(provider));
        var target = await SessionAsync(provider, "target", null);
        using var cancellation = new CancellationTokenSource();
        var progress = new Inline<DataTransferProgress>(p =>
        {
            if (p.TableIndex == 0 && p.Rows >= 2) cancellation.Cancel();
        });

        var outcomes = await DataTransferJob.RunAsync(source, target, Identities("customers", "notes"),
            new DataTransferOptions { BatchSize = 2 }, progress, cancellation.Token);

        Assert.Equal([DataTransferTableStatus.Canceled, DataTransferTableStatus.Canceled], outcomes.Select(o => o.Status));
        Assert.Contains("rolled back", outcomes[0].Diagnostic);
        Assert.Equal("Not started.", outcomes[1].Diagnostic);
        // The table created inside the canceled transaction is gone with it.
        Assert.Equal("", await QueryAsync(target, provider == "SQLite"
            ? "SELECT name FROM sqlite_master WHERE type = 'table'"
            : "SELECT table_name FROM duckdb_tables()"));
    }

    [Fact]
    public async Task ATableThatCannotBeReadFailsWithItsReason()
    {
        var source = await SessionAsync("SQLite", "source", SqliteFixture);
        var target = await SessionAsync("SQLite", "target", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, Identities("missing", "notes"), new DataTransferOptions());

        Assert.Equal(DataTransferTableStatus.Failed, outcomes[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(outcomes[0].Diagnostic));
        Assert.Equal(DataTransferTableStatus.Created, outcomes[1].Status);
    }

    [Fact]
    public void TheSameDatabaseIsRefusedAndAnotherEngineIsAccepted()
    {
        var sqlite = Session("SQLite", "a");
        Assert.Null(DataTransferJob.Refusal(sqlite, Session("DuckDB", "b")));
        Assert.Contains("same database", DataTransferJob.Refusal(sqlite, Session("SQLite", "a")));
        Assert.Null(DataTransferJob.Refusal(sqlite, Session("SQLite", "b")));
    }

    /// <summary>Reports on the calling thread, so the test can cancel at an exact point.</summary>
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
