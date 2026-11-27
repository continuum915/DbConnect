using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>The designer reads complete SQLite table designs (issue 06).</summary>
public sealed class TableDesignSqliteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-sqlite-design-" + Guid.NewGuid().ToString("N"));

    public TableDesignSqliteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    internal const string Fixture = """
        CREATE TABLE "Order.Items" (
            "Tenant" INTEGER NOT NULL,
            id INTEGER NOT NULL,
            parent_id INTEGER,
            qty INTEGER NOT NULL DEFAULT 1 CONSTRAINT qty_positive CHECK (qty > 0),
            "Name" TEXT COLLATE NOCASE,
            code TEXT UNIQUE,
            total INTEGER GENERATED ALWAYS AS (qty * 2) STORED,
            half REAL AS (qty / 2.0),
            note TEXT DEFAULT 'none',
            CONSTRAINT "PK Items" PRIMARY KEY ("Tenant", id),
            CONSTRAINT uq_note UNIQUE (note, qty),
            CHECK (qty < 1000 OR note IS NOT NULL),
            CONSTRAINT fk_self FOREIGN KEY ("Tenant", parent_id) REFERENCES "Order.Items" ("Tenant", id)
                ON DELETE CASCADE ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED
        ) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_expr ON "Order.Items"(lower(code) DESC);
        CREATE INDEX "ix.partial" ON "Order.Items"(qty DESC, "Name" COLLATE BINARY) WHERE note IS NOT NULL;
        CREATE TRIGGER trg_items AFTER UPDATE OF qty ON "Order.Items" BEGIN SELECT 1; END;
        CREATE TABLE counter (id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT REFERENCES plain);
        CREATE TABLE plain (id INTEGER PRIMARY KEY, v TEXT);
        CREATE TABLE odd (a INTEGER NOT NULL ON CONFLICT REPLACE);
        CREATE VIRTUAL TABLE docs USING fts5(body);
        """;

    internal static readonly string[] Expected =
    [
        "table main.Order.Items",
        "column Tenant INTEGER NOT NULL",
        "column id INTEGER NOT NULL",
        "column parent_id INTEGER",
        "column qty INTEGER NOT NULL default=1",
        "column Name TEXT collate=NOCASE",
        "column code TEXT",
        "column total INTEGER generated=AS (qty * 2) STORED",
        "column half REAL generated=AS (qty / 2.0) VIRTUAL",
        "column note TEXT default='none'",
        "primary key PK Items (Tenant, id)",
        "unique (unnamed) (code)",
        "unique uq_note (note, qty)",
        "foreign key fk_self (Tenant -> Tenant, parent_id -> id) references main.Order.Items on delete CASCADE on update RESTRICT state=DEFERRABLE INITIALLY DEFERRED",
        "referenced by fk_self from main.Order.Items (Tenant, id)",
        "check qty_positive column=qty qty > 0",
        "check (unnamed) qty < 1000 OR note IS NOT NULL",
        "index ix.partial CreateIndex (qty DESC, Name COLLATE BINARY) where=note IS NOT NULL",
        "index ix_expr CreateIndex ((lower(code)) DESC)",
        "index sqlite_autoindex_Order.Items_1 unique UniqueKey (code)",
        "index sqlite_autoindex_Order.Items_2 unique PrimaryKey (Tenant, id)",
        "index sqlite_autoindex_Order.Items_3 unique UniqueKey (note, qty)",
        "trigger trg_items AFTER UPDATE OF qty FOR EACH ROW",
        "option WITHOUT ROWID=true",
        "option STRICT=true",
        "unsupported ColumnComments: Not supported by SQLite",
        "unsupported TableComment: Not supported by SQLite",
        "unsupported IndexIncludedColumns: Not supported by SQLite",
        "unsupported IndexMethod: Not supported by SQLite",
    ];

    internal string Database(string name, string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, name + ".db"), Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    [Fact]
    public async Task Design_ReadsEveryElementAndAgreesWithTheScript()
    {
        var cs = Database("main", Fixture);
        var provider = new SqliteProvider();
        var identity = new DatabaseObjectIdentity(null, null, "Order.Items", SchemaNodeType.Table);
        var design = await provider.GetTableDesignAsync(cs, identity);
        TableDesignConsistency.AssertSame(Expected, design);
        Assert.Contains("SELECT 1", Assert.Single(design.Triggers).Definition);
        TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity), design);

        var counter = await provider.GetTableDesignAsync(cs, identity with { Name = "counter" });
        Assert.Equal(TableDesignIdentityKind.AutoIncrement, counter.Columns[0].Identity?.Kind);
        Assert.Equal("id", Assert.Single(counter.ForeignKeys).Columns[0].ReferencedColumn);
        var plain = await provider.GetTableDesignAsync(cs, identity with { Name = "plain" });
        Assert.Equal(TableDesignIdentityKind.RowIdAlias, plain.Columns[0].Identity?.Kind);
        foreach (var name in new[] { "counter", "plain" })
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = name }),
                await provider.GetTableDesignAsync(cs, identity with { Name = name }));

        var odd = await provider.GetTableDesignAsync(cs, identity with { Name = "odd" });
        TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = "odd" }), odd);
        Assert.Contains(odd.Unrepresented, g => g.Description.Contains("ON CONFLICT REPLACE"));
        var docs = await provider.GetTableDesignAsync(cs, identity with { Name = "docs" });
        Assert.Contains(docs.Unrepresented, g => g.Description.Contains("virtual table"));
        await Assert.ThrowsAsync<TableDefinitionUnavailableException>(() => provider.GetTableDesignAsync(cs, identity with { Name = "missing" }));
    }

    [Fact]
    public async Task AttachedSchema_ReadsItsOwnSameNamedTable()
    {
        var main = Database("main", Fixture);
        var other = Database("other", """CREATE TABLE "Order.Items" (elsewhere INTEGER PRIMARY KEY);""");
        await using var connection = new SqliteConnection(main);
        await connection.OpenAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"ATTACH DATABASE '{new SqliteConnectionStringBuilder(other).DataSource.Replace("'", "''")}' AS \"aux.db\";";
            await cmd.ExecuteNonQueryAsync();
        }
        var attached = await SqliteTableDesignReader.LoadAsync(connection,
            new DatabaseObjectIdentity(null, "aux.db", "Order.Items", SchemaNodeType.Table), CancellationToken.None);
        Assert.Equal("aux.db", attached.Table.Schema);
        Assert.Equal("elsewhere", Assert.Single(attached.Columns).Name);
        var own = await SqliteTableDesignReader.LoadAsync(connection,
            new DatabaseObjectIdentity(null, "main", "Order.Items", SchemaNodeType.Table), CancellationToken.None);
        Assert.Equal(9, own.Columns.Count);
    }
}
