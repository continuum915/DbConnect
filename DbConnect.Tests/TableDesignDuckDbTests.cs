using DuckDB.NET.Data;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>The designer reads complete DuckDB table designs (issue 06).</summary>
public sealed class TableDesignDuckDbTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-duck-design-" + Guid.NewGuid().ToString("N"));

    public TableDesignDuckDbTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    internal const string Fixture = """
        CREATE SCHEMA "s.x";
        CREATE TABLE "s.x"."Order.Items" (
            "Tenant" INTEGER NOT NULL,
            id INTEGER NOT NULL,
            parent_id INTEGER,
            qty DECIMAL(10,2) NOT NULL DEFAULT 1 CHECK (qty > 0),
            "Name" VARCHAR COLLATE NOCASE,
            code VARCHAR,
            total DECIMAL(12,2) GENERATED ALWAYS AS (qty * 2) VIRTUAL,
            note VARCHAR DEFAULT 'none',
            CONSTRAINT pk_items PRIMARY KEY ("Tenant", id),
            UNIQUE ("Tenant", code),
            CHECK (qty < 1000 OR note IS NOT NULL),
            FOREIGN KEY ("Tenant", parent_id) REFERENCES "s.x"."Order.Items" ("Tenant", id)
        );
        CREATE INDEX "ix.expr" ON "s.x"."Order.Items" (lower(code), qty);
        CREATE UNIQUE INDEX ix_note ON "s.x"."Order.Items" (note);
        COMMENT ON TABLE "s.x"."Order.Items" IS 'it''s the items';
        COMMENT ON COLUMN "s.x"."Order.Items".qty IS 'how many';
        COMMENT ON INDEX "s.x"."ix.expr" IS 'expression';
        CREATE TABLE main."Order.Items" (elsewhere INTEGER);
        """;

    internal static readonly string[] Expected =
    [
        "table s.x.Order.Items",
        "column Tenant INTEGER NOT NULL",
        "column id INTEGER NOT NULL",
        "column parent_id INTEGER",
        "column qty DECIMAL(10,2) NOT NULL default=1 comment=how many",
        "column Name VARCHAR collate=NOCASE",
        "column code VARCHAR",
        "column total DECIMAL(12,2) generated=AS ((qty * 2)) VIRTUAL",
        "column note VARCHAR default='none'",
        "primary key (unnamed) (Tenant, id)",
        "unique (unnamed) (Tenant, code)",
        "foreign key (unnamed) (Tenant -> Tenant, parent_id -> id) references s.x.Order.Items on delete NO ACTION on update NO ACTION",
        "referenced by (unnamed) from s.x.Order.Items (Tenant, id)",
        "check (unnamed) column=qty (qty > 0)",
        "check (unnamed) ((qty < 1000) OR (note IS NOT NULL))",
        "index ix.expr CreateIndex (((lower(code))), qty) comment=expression",
        "index ix_note unique CreateIndex (note)",
        "comment it's the items",
        "unsupported Identity: Not supported by DuckDB",
        "unsupported StoredGeneratedColumns: Not supported by DuckDB",
        "unsupported ForeignKeyActions: Not supported by DuckDB",
        "unsupported Triggers: Not supported by DuckDB",
        "unsupported Options: Not supported by DuckDB",
        "unsupported IndexIncludedColumns: Not supported by DuckDB",
        "unsupported IndexFilter: Not supported by DuckDB",
        "unsupported IndexDescending: Not supported by DuckDB",
        "unsupported IndexMethod: Not supported by DuckDB",
        "unsupported ConstraintChanges: Not supported by DuckDB",
    ];

    internal string Database(string sql)
    {
        var cs = $"Data Source={Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".duckdb")}";
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    [Fact]
    public async Task Design_ReadsEveryElementAndAgreesWithTheScript()
    {
        var cs = Database(Fixture);
        var provider = new DuckDbProvider();
        var identity = new DatabaseObjectIdentity(null, "s.x", "Order.Items", SchemaNodeType.Table);
        var design = await provider.GetTableDesignAsync(cs, identity);
        TableDesignConsistency.AssertSame(Expected, design);
        TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity), design);

        var other = await provider.GetTableDesignAsync(cs, identity with { Schema = "main" });
        TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Schema = "main" }), other);
        Assert.Equal("elsewhere", Assert.Single(other.Columns).Name);
        await Assert.ThrowsAsync<TableDefinitionUnavailableException>(() => provider.GetTableDesignAsync(cs, identity with { Name = "missing" }));
    }
}
