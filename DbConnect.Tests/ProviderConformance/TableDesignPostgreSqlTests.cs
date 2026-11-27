using Npgsql;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>The designer reads complete PostgreSQL table designs (issue 06).</summary>
public sealed class TableDesignPostgreSqlTests
{
    internal static string Fixture(string s) => $$"""
        CREATE SCHEMA "{{s}}";
        CREATE SCHEMA "{{s}}_b";
        CREATE FUNCTION "{{s}}".touch() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END $f$;
        CREATE TABLE "{{s}}"."Order.Items" (
            "Tenant" int NOT NULL,
            id bigint GENERATED ALWAYS AS IDENTITY (START WITH 10 INCREMENT BY 2),
            parent_id bigint,
            serial_no serial,
            qty int NOT NULL DEFAULT 1 CONSTRAINT qty_positive CHECK (qty > 0),
            "Name" text COLLATE "C",
            code text,
            total int GENERATED ALWAYS AS (qty * 2) STORED,
            note varchar(80),
            CONSTRAINT "PK Items" PRIMARY KEY ("Tenant", id),
            CONSTRAINT uq_code UNIQUE ("Tenant", code),
            UNIQUE (note, qty),
            CONSTRAINT fk_parent FOREIGN KEY ("Tenant", parent_id) REFERENCES "{{s}}"."Order.Items" ("Tenant", id)
                ON DELETE CASCADE ON UPDATE RESTRICT,
            CHECK (qty < 1000 OR note IS NOT NULL)
        ) WITH (fillfactor = 80);
        CREATE INDEX ix_expr ON "{{s}}"."Order.Items" (lower(code));
        CREATE INDEX "ix.partial" ON "{{s}}"."Order.Items" (qty DESC NULLS LAST) INCLUDE ("Name") WHERE note IS NOT NULL;
        CREATE INDEX ix_hash ON "{{s}}"."Order.Items" USING hash (code);
        CREATE TRIGGER trg_touch BEFORE INSERT OR UPDATE ON "{{s}}"."Order.Items" FOR EACH ROW EXECUTE FUNCTION "{{s}}".touch();
        COMMENT ON TABLE "{{s}}"."Order.Items" IS 'it''s the items';
        COMMENT ON COLUMN "{{s}}"."Order.Items".qty IS 'how many';
        COMMENT ON INDEX "{{s}}"."ix.partial" IS 'partial';
        ALTER TABLE "{{s}}"."Order.Items" ADD CONSTRAINT ck_note CHECK (length(note) < 50) NOT VALID;
        CREATE TABLE "{{s}}"."order.items" (other int);
        CREATE TABLE "{{s}}_b"."Order.Items" (elsewhere int);
        CREATE TABLE "{{s}}".measurements (at date NOT NULL, v int) PARTITION BY RANGE (at);
        CREATE TABLE "{{s}}".measurements_2026 PARTITION OF "{{s}}".measurements FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        """;

    internal static string[] Expected(string s) =>
    [
        $"table {s}.Order.Items",
        "column Tenant integer NOT NULL",
        "column id bigint NOT NULL identity=Always(10, 2)",
        "column parent_id bigint",
        $"column serial_no integer NOT NULL default=nextval('\"{s}\".\"Order.Items_serial_no_seq\"'::regclass) sequence=owns sequence \"{s}\".\"Order.Items_serial_no_seq\"",
        "column qty integer NOT NULL default=1 comment=how many",
        "column Name text collate=\"C\"",
        "column code text",
        "column total integer generated=AS (qty * 2) STORED",
        "column note character varying(80)",
        "primary key PK Items (Tenant, id)",
        "unique Order.Items_note_qty_key (note, qty)",
        "unique uq_code (Tenant, code)",
        $"foreign key fk_parent (Tenant -> Tenant, parent_id -> id) references {s}.Order.Items on delete CASCADE on update RESTRICT",
        $"referenced by fk_parent from {s}.Order.Items (Tenant, id)",
        "check Order.Items_check qty < 1000 OR note IS NOT NULL",
        "check ck_note column=note length(note::text) < 50 state=NOT VALID",
        "check qty_positive column=qty qty > 0",
        "index Order.Items_note_qty_key unique UniqueKey (note, qty) using=btree",
        "index PK Items unique PrimaryKey (Tenant, id) using=btree",
        "index ix.partial CreateIndex (qty DESC NULLS LAST) include (Name) where=note IS NOT NULL using=btree comment=partial",
        "index ix_expr CreateIndex ((lower(code))) using=btree",
        "index ix_hash CreateIndex (code) using=hash",
        "index uq_code unique UniqueKey (Tenant, code) using=btree",
        "trigger trg_touch BEFORE INSERT OR UPDATE FOR EACH ROW",
        "option fillfactor=80",
        "comment it's the items",
        "unsupported VirtualGeneratedColumns: Not supported by PostgreSQL 16",
        "unsupported GeneratedExpressionChanges: Not supported by PostgreSQL 16"
    ];

    internal static async Task Execute(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static Task Drop(string cs, string s) =>
        Execute(cs, $"DROP SCHEMA IF EXISTS \"{s}\" CASCADE; DROP SCHEMA IF EXISTS \"{s}_b\" CASCADE;");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Design_ReadsEveryElementAndAgreesWithTheScript()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.design_{Guid.NewGuid().ToString("N")[..10]}";
        var provider = new PostgreSqlProvider();
        try
        {
            await Execute(cs, Fixture(schema));
            var identity = new DatabaseObjectIdentity(null, schema, "Order.Items", SchemaNodeType.Table);
            var design = await provider.GetTableDesignAsync(cs, identity);
            TableDesignConsistency.AssertSame(Expected(schema), design);
            Assert.Contains("EXECUTE FUNCTION", Assert.Single(design.Triggers).Definition);
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity), design);

            var lower = await provider.GetTableDesignAsync(cs, identity with { Name = "order.items" });
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = "order.items" }), lower);
            Assert.Equal("other", Assert.Single(lower.Columns).Name);
            var other = await provider.GetTableDesignAsync(cs, identity with { Schema = schema + "_b" });
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Schema = schema + "_b" }), other);
            Assert.Equal("elsewhere", Assert.Single(other.Columns).Name);

            var partitioned = await provider.GetTableDesignAsync(cs, identity with { Name = "measurements" });
            Assert.Contains(partitioned.Unrepresented, g => g.Description.Contains("partitioned by RANGE (at)"));
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = "measurements" }), partitioned);
            var partition = await provider.GetTableDesignAsync(cs, identity with { Name = "measurements_2026" });
            Assert.Contains(partition.Unrepresented, g => g.Description.Contains("partition of"));
            await Assert.ThrowsAsync<TableDefinitionUnavailableException>(() =>
                provider.GetTableDesignAsync(cs, identity with { Name = "missing" }));
        }
        finally
        {
            await Drop(cs, schema);
        }
    }
}
