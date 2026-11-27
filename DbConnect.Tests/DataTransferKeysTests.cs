using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Dependency order, foreign keys and indexes in a data transfer (data-transfer/04).</summary>
public sealed class DataTransferKeysTests : DumpTestDatabases
{
    // Chosen child first: orders references customers, and DuckDB checks each row's key as it goes in.
    private const string Chain = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), total DECIMAL(10, 2));
        CREATE INDEX orders_customer ON orders(customer_id);
        CREATE UNIQUE INDEX customers_name ON customers(name);
        INSERT INTO customers VALUES (1, 'Ann'), (2, 'Bo');
        INSERT INTO orders VALUES (10, 1, 5.5), (11, 2, 7), (12, 2, 1);
        """;

    private sealed class Recorder : IProgress<DataTransferProgress>
    {
        public List<string> Tables { get; } = [];
        public void Report(DataTransferProgress value)
        {
            if (Tables.LastOrDefault() != value.Table.Name) Tables.Add(value.Table.Name);
        }
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task ReferencedTablesAreCopiedFirstWithTheirIndexesAndKeys(string provider)
    {
        var source = await SessionAsync(provider, "source", Chain);
        var target = await SessionAsync(provider, "target", null);
        var order = new Recorder();

        var outcomes = await DataTransferJob.RunAsync(source, target, [Table("orders"), Table("customers")],
            new DataTransferOptions(), order);

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Diagnostic}"));
        // Outcomes stay in the order chosen; the copy ran parents first.
        Assert.Equal(["orders", "customers"], outcomes.Select(o => o.Table.Name));
        Assert.Equal(["customers", "orders"], order.Tables);
        foreach (var table in new[] { "customers", "orders" })
        {
            Assert.Equal(await SnapshotAsync(source, table), await SnapshotAsync(target, table));
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        }
    }

    [Fact]
    public async Task ACycleAndASelfReferenceLoadWithForeignKeyChecksOn()
    {
        var source = await SessionAsync("SQLite", "source", """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE b_teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL, captain_id INTEGER REFERENCES a_people(id));
            CREATE TABLE a_people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, team_id INTEGER REFERENCES b_teams(id));
            CREATE TABLE c_nodes (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES c_nodes(id), label TEXT);
            INSERT INTO b_teams VALUES (1, 'red', 2), (2, 'blue', NULL);
            INSERT INTO a_people VALUES (1, 'Ann', 2), (2, 'Bo', 1);
            INSERT INTO c_nodes VALUES (1, 3, 'leaf'), (2, NULL, 'root'), (3, 2, 'branch');
            """);
        // The target enforces foreign keys on every connection.
        var target = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "target",
            Provider = "SQLite",
            ConnectionString = $"Data Source={Path.Combine(DumpDirectory, "target.sqlite")};Foreign Keys=True"
        });

        var outcomes = await DataTransferJob.RunAsync(source, target, [Table("a_people"), Table("b_teams"), Table("c_nodes")],
            new DataTransferOptions { BatchSize = 1 });

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Diagnostic}"));
        foreach (var table in new[] { "a_people", "b_teams", "c_nodes" })
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        Assert.Equal("", await QueryAsync(target, "PRAGMA foreign_key_check"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task AKeyToATableOutsideTheTransferIsReported(string provider)
    {
        var source = await SessionAsync(provider, "source", Chain);
        var target = await SessionAsync(provider, "target", null);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Table("orders")], new DataTransferOptions()));

        Assert.Contains(outcome.Notes, n => n.Contains("references main.customers, which is not part of the transfer") ||
                                            n.Contains("references customers, which is not part of the transfer"));
        // SQLite creates the table with the dangling key; DuckDB needs the referenced table to exist.
        Assert.Equal(provider == "SQLite" ? DataTransferTableStatus.Created : DataTransferTableStatus.Failed, outcome.Status);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task IndexesCanBeLeftOut(string provider)
    {
        var source = await SessionAsync(provider, "source", Chain);
        var target = await SessionAsync(provider, "target", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, [Table("customers"), Table("orders")],
            new DataTransferOptions { IncludeIndexes = false });

        Assert.All(outcomes, o => Assert.Contains("Indexes were not created (turned off).", o.Notes));
        Assert.Equal("", await QueryAsync(target, provider == "SQLite"
            ? "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL"
            : "SELECT index_name FROM duckdb_indexes()"));
        Assert.Equal(await RowsAsync(source, "orders"), await RowsAsync(target, "orders"));
    }
}
