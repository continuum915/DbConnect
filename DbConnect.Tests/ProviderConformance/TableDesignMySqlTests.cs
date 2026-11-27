using MySqlConnector;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>The designer reads complete MySQL table designs (issue 06).</summary>
public sealed class TableDesignMySqlTests
{
    /// <summary>Tables go in rain_scratch_* databases, which the integration login may create (build/databases/mysql).</summary>
    internal static string Fixture(string db) => $$"""
        CREATE DATABASE `{{db}}`;
        CREATE DATABASE `{{db}}_b`;
        CREATE TABLE `{{db}}`.`Order.Items` (
            `Tenant` INT NOT NULL,
            id INT UNSIGNED NOT NULL AUTO_INCREMENT,
            parent_id INT UNSIGNED,
            qty DECIMAL(10,2) NOT NULL DEFAULT 1.00 COMMENT 'how many' CHECK (qty > 0),
            `Name` VARCHAR(40) COLLATE utf8mb4_bin,
            code VARCHAR(20),
            total DECIMAL(12,2) GENERATED ALWAYS AS (qty * 2) STORED,
            half DECIMAL(12,2) AS (qty / 2) VIRTUAL,
            note VARCHAR(80) DEFAULT 'none',
            changed TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (`Tenant`, id),
            UNIQUE KEY id_key (id),
            CONSTRAINT uq_code UNIQUE (`Tenant`, code),
            UNIQUE (note),
            CONSTRAINT ck_note CHECK (char_length(note) < 50 OR qty > 1),
            CONSTRAINT fk_self FOREIGN KEY (`Tenant`, parent_id) REFERENCES `Order.Items` (`Tenant`, id)
                ON DELETE CASCADE ON UPDATE RESTRICT,
            INDEX `ix.desc` (qty DESC, code(5)) COMMENT 'partial',
            INDEX ix_expr ((lower(code)))
        ) ENGINE=InnoDB COMMENT='it''s the items' ROW_FORMAT=DYNAMIC;
        CREATE TRIGGER `{{db}}`.trg_items BEFORE INSERT ON `{{db}}`.`Order.Items` FOR EACH ROW SET NEW.note = coalesce(NEW.note, 'filled');
        CREATE TABLE `{{db}}`.`order.items` (other INT);
        CREATE TABLE `{{db}}_b`.`Order.Items` (elsewhere INT);
        CREATE TABLE `{{db}}`.parted (id INT NOT NULL PRIMARY KEY) PARTITION BY HASH (id) PARTITIONS 2;
        """;

    internal static string[] Expected(string db) =>
    [
        $"table {db}.Order.Items",
        "column Tenant int NOT NULL",
        "column id int unsigned NOT NULL identity=AutoIncrement",
        "column parent_id int unsigned",
        "column qty decimal(10,2) NOT NULL default=1.00 comment=how many",
        "column Name varchar(40) collate=utf8mb4_bin",
        "column code varchar(20)",
        "column total decimal(12,2) generated=AS ((`qty` * 2)) STORED",
        "column half decimal(12,2) generated=AS ((`qty` / 2)) VIRTUAL",
        "column note varchar(80) default='none'",
        "column changed timestamp default=CURRENT_TIMESTAMP [ON UPDATE CURRENT_TIMESTAMP]",
        "primary key (unnamed) (Tenant, id)",
        "unique id_key (id)",
        "unique note (note)",
        "unique uq_code (Tenant, code)",
        $"foreign key fk_self (Tenant -> Tenant, parent_id -> id) references {db}.Order.Items on delete CASCADE on update RESTRICT",
        $"referenced by fk_self from {db}.Order.Items (Tenant, id)",
        "check ck_note ((char_length(`note`) < 50) or (`qty` > 1))",
        "check Order.Items_chk_1 (`qty` > 0)",
        "index PRIMARY unique PrimaryKey (Tenant, id) using=BTREE",
        "index fk_self CreateIndex (Tenant, parent_id) using=BTREE",
        "index id_key unique UniqueKey (id) using=BTREE",
        "index ix.desc CreateIndex (qty DESC, code PREFIX 5) using=BTREE comment=partial",
        "index ix_expr CreateIndex ((lower(`code`))) using=BTREE",
        "index note unique UniqueKey (note) using=BTREE",
        "index uq_code unique UniqueKey (Tenant, code) using=BTREE",
        "trigger trg_items BEFORE INSERT FOR EACH ROW",
        "option ENGINE=InnoDB",
        "option COLLATE=utf8mb4_0900_ai_ci",
        "option ROW_FORMAT=DYNAMIC",
        "comment it's the items",
        "unsupported IndexIncludedColumns: Not supported by MySQL",
        "unsupported IndexFilter: Not supported by MySQL",
    ];

    internal static async Task Execute(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        foreach (var statement in SqlBatchParser.SplitStatements(sql, SqlBatchDialect.MySql))
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            await using var cmd = new MySqlCommand(statement, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    internal static Task Drop(string cs, string db) => Execute(cs, $"DROP DATABASE IF EXISTS `{db}`; DROP DATABASE IF EXISTS `{db}_b`;");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Design_ReadsEveryElementAndAgreesWithTheScript()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var provider = new MySqlProvider();
        try
        {
            await Execute(cs, Fixture(db));
            var identity = new DatabaseObjectIdentity(db, null, "Order.Items", SchemaNodeType.Table);
            var design = await provider.GetTableDesignAsync(cs, identity);
            TableDesignConsistency.AssertSame(Expected(db), design);
            Assert.Contains("coalesce", Assert.Single(design.Triggers).Definition);
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity), design);

            var lower = await provider.GetTableDesignAsync(cs, identity with { Name = "order.items" });
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = "order.items" }), lower);
            Assert.Equal("other", Assert.Single(lower.Columns).Name);
            var other = await provider.GetTableDesignAsync(cs, identity with { Catalog = db + "_b" });
            Assert.Equal("elsewhere", Assert.Single(other.Columns).Name);
            var parted = await provider.GetTableDesignAsync(cs, identity with { Name = "parted" });
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Name = "parted" }), parted);
            Assert.Contains(parted.Unrepresented, g => g.Description.Contains("partitioned"));
            await Assert.ThrowsAsync<TableDefinitionUnavailableException>(() =>
                provider.GetTableDesignAsync(cs, identity with { Name = "missing" }));
        }
        finally
        {
            await Drop(cs, db);
        }
    }
}
