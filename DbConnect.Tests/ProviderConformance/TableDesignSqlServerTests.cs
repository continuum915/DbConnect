using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>The designer reads complete SQL Server table designs (issue 06).</summary>
public sealed partial class TableDesignSqlServerTests
{
    internal static string Fixture(string s) => $"""
        CREATE SCHEMA [{s}];
        GO
        CREATE SCHEMA [{s}_b];
        GO
        CREATE TABLE [{s}].[parent.t] (id int NOT NULL PRIMARY KEY);
        CREATE TABLE [{s}].[Order.Items] (
            [Tenant] int NOT NULL,
            id int IDENTITY(10, 2) NOT NULL,
            parent_id int NULL,
            manager_id int NULL,
            qty decimal(10, 2) NOT NULL CONSTRAINT df_qty DEFAULT (1) CHECK (qty > 0),
            [Name] nvarchar(40) COLLATE Latin1_General_CS_AS NULL,
            code nvarchar(20) NULL,
            total AS (qty * 2) PERSISTED,
            half AS (qty / 2),
            note nvarchar(80) NULL DEFAULT (N'none'),
            PRIMARY KEY ([Tenant], id),
            CONSTRAINT uq_code UNIQUE ([Tenant], code),
            UNIQUE (note),
            CONSTRAINT ck_note CHECK (len(note) < 50 OR qty > 1),
            CONSTRAINT fk_self FOREIGN KEY ([Tenant], manager_id) REFERENCES [{s}].[Order.Items] ([Tenant], id),
            CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES [{s}].[parent.t] (id) ON DELETE CASCADE ON UPDATE SET NULL
        ) WITH (DATA_COMPRESSION = PAGE);
        GO
        ALTER TABLE [{s}].[Order.Items] SET (LOCK_ESCALATION = AUTO);
        ALTER TABLE [{s}].[Order.Items] WITH NOCHECK ADD CONSTRAINT ck_name CHECK (len([Name]) < 30);
        CREATE NONCLUSTERED INDEX [ix.filtered] ON [{s}].[Order.Items] (qty DESC, code) INCLUDE ([Name]) WHERE ([Name] IS NOT NULL) WITH (FILLFACTOR = 80);
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'it''s the items', @level0type = N'SCHEMA', @level0name = N'{s}', @level1type = N'TABLE', @level1name = N'Order.Items';
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'how many', @level0type = N'SCHEMA', @level0name = N'{s}', @level1type = N'TABLE', @level1name = N'Order.Items', @level2type = N'COLUMN', @level2name = N'qty';
        GO
        CREATE TRIGGER [{s}].[trg_items] ON [{s}].[Order.Items] AFTER INSERT, UPDATE AS
        BEGIN
            SET NOCOUNT ON;
        END
        GO
        CREATE TABLE [{s}_b].[Order.Items] (elsewhere int);
        CREATE TABLE [{s}].[Versioned] (
            id int NOT NULL PRIMARY KEY,
            vf datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
            vt datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (vf, vt)
        ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [{s}].[VersionedHistory]));
        GO
        """;

    internal static string[] Expected(string s) =>
    [
        $"table rain_ecommerce.{s}.Order.Items",
        "column Tenant int NOT NULL",
        "column id int NOT NULL identity=Identity(10, 2)",
        "column parent_id int",
        "column manager_id int",
        "column qty decimal(10, 2) NOT NULL default=((1)) comment=how many [DEFAULT CONSTRAINT df_qty]",
        "column Name nvarchar(40) collate=Latin1_General_CS_AS",
        "column code nvarchar(20)",
        "column total decimal(12, 2) generated=AS (([qty]*(2))) STORED",
        "column half decimal(14, 6) generated=AS (([qty]/(2))) VIRTUAL",
        "column note nvarchar(80) default=(N'none')",
        "primary key (unnamed) (Tenant, id)",
        "unique uq_code (Tenant, code)",
        "unique (unnamed) (note)",
        $"foreign key fk_parent (parent_id -> id) references rain_ecommerce.{s}.parent.t on delete CASCADE on update SET NULL",
        $"foreign key fk_self (Tenant -> Tenant, manager_id -> id) references rain_ecommerce.{s}.Order.Items on delete NO ACTION on update NO ACTION",
        $"referenced by fk_self from rain_ecommerce.{s}.Order.Items (Tenant, id)",
        "check ck_name column=Name (len([Name])<(30)) state=NOT TRUSTED",
        "check ck_note (len([note])<(50) OR [qty]>(1))",
        "check (unnamed) column=qty ([qty]>(0))",
        "index ix.filtered CreateIndex (qty DESC, code) include (Name) where=([Name] IS NOT NULL) using=NONCLUSTERED [FILLFACTOR = 80]",
        "index PK__* unique PrimaryKey (Tenant, id) using=CLUSTERED [DATA_COMPRESSION = PAGE]",
        "index UQ__* unique UniqueKey (note) using=NONCLUSTERED",
        "index uq_code unique UniqueKey (Tenant, code) using=NONCLUSTERED",
        "trigger trg_items AFTER INSERT OR UPDATE",
        "option DATA_COMPRESSION=PAGE",
        "option LOCK_ESCALATION=AUTO",
        "comment it's the items",
        "unsupported IndexExpressions: Not supported by SQL Server",
    ];

    internal static async Task Execute(string cs, string sql)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        foreach (var batch in SqlBatchParser.SplitStatements(sql, SqlBatchDialect.SqlServer))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = new SqlCommand(batch, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    internal static Task Drop(string cs, string s) => Execute(cs, $"""
        IF OBJECT_ID(N'[{s}].[Versioned]') IS NOT NULL ALTER TABLE [{s}].[Versioned] SET (SYSTEM_VERSIONING = OFF);
        DROP TABLE IF EXISTS [{s}].[Versioned], [{s}].[VersionedHistory], [{s}].[Order.Items], [{s}].[parent.t], [{s}_b].[Order.Items];
        IF SCHEMA_ID(N'{s}') IS NOT NULL EXEC (N'DROP SCHEMA [{s}]');
        IF SCHEMA_ID(N'{s}_b') IS NOT NULL EXEC (N'DROP SCHEMA [{s}_b]');
        """);

    /// <summary>Names SQL Server generates for unnamed keys end in a random suffix.</summary>
    internal static IReadOnlyList<string> Normalized(TableDesign design) =>
        TableDesignText.Lines(design).Select(l => GeneratedName().Replace(l, "$1__*")).ToList();

    [GeneratedRegex(@"\b(PK|UQ)__[A-Za-z0-9_.]*?__[0-9A-F]{16}")]
    private static partial Regex GeneratedName();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Design_ReadsEveryElementAndAgreesWithTheScript()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.design_{Guid.NewGuid().ToString("N")[..10]}";
        var provider = new SqlServerProvider();
        try
        {
            await Execute(cs, Fixture(schema));
            var identity = new DatabaseObjectIdentity(null, schema, "Order.Items", SchemaNodeType.Table);
            var design = await provider.GetTableDesignAsync(cs, identity);
            var lines = Normalized(design);
            Assert.True(Expected(schema).SequenceEqual(lines), "Actual:\n  " + string.Join("\n  ", lines));
            Assert.Contains("SET NOCOUNT ON", Assert.Single(design.Triggers).Definition);
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity), design);

            var other = await provider.GetTableDesignAsync(cs, identity with { Schema = schema + "_b" });
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, identity with { Schema = schema + "_b" }), other);
            var parent = identity with { Name = "parent.t" };
            TableDesignConsistency.AssertConsistent(await provider.GetTableSnapshotAsync(cs, parent), await provider.GetTableDesignAsync(cs, parent));
            Assert.Equal("elsewhere", Assert.Single(other.Columns).Name);
            var versioned = await provider.GetTableDesignAsync(cs, identity with { Name = "Versioned" });
            Assert.Contains(versioned.Unrepresented, g => g.Description.Contains("system-versioned (temporal)"));
            await Assert.ThrowsAsync<TableDefinitionUnavailableException>(() =>
                provider.GetTableDesignAsync(cs, identity with { Name = "missing" }));
        }
        finally
        {
            await Drop(cs, schema);
        }
    }
}
