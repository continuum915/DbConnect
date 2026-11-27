using Microsoft.Data.SqlClient;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Script as CREATE recreates SQL Server tables in an isolated database (§1.6, issue 05).</summary>
public sealed class TableScriptingSqlServerTests
{
    private static string Prerequisites(string s) => $"""
        CREATE SCHEMA [{s}];
        GO
        CREATE TYPE [{s}].[Code] FROM nvarchar(12) NOT NULL;
        GO
        CREATE TABLE [{s}].[parent.t] (id int NOT NULL PRIMARY KEY, code nvarchar(20) UNIQUE);
        CREATE TABLE [{s}].[audit] (item_id int, action nvarchar(10));
        GO
        """;

    private static string Fixture(string s) => Prerequisites(s) + Environment.NewLine + $"""
        CREATE TABLE [{s}].[Order Items] (
            id int IDENTITY(100, 5) NOT NULL,
            parent_id int NULL,
            manager_id int NULL,
            qty decimal(10, 2) NOT NULL CONSTRAINT df_qty DEFAULT (1),
            price money NOT NULL DEFAULT (0),
            total AS (qty * 2) PERSISTED,
            [Name] nvarchar(40) COLLATE Latin1_General_CS_AS NULL,
            code [{s}].[Code],
            stamp datetime2(3) NULL,
            ratio float(24) NULL,
            blob varbinary(max) NULL,
            CONSTRAINT pk_items PRIMARY KEY CLUSTERED (id DESC),
            CONSTRAINT uq_items UNIQUE NONCLUSTERED (parent_id, [Name]),
            CONSTRAINT ck_qty CHECK (qty > 0),
            CHECK (price >= 0)
        );
        GO
        ALTER TABLE [{s}].[Order Items] ADD CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES [{s}].[parent.t] (id) ON DELETE CASCADE;
        ALTER TABLE [{s}].[Order Items] ADD CONSTRAINT fk_self FOREIGN KEY (manager_id) REFERENCES [{s}].[Order Items] (id);
        ALTER TABLE [{s}].[Order Items] WITH NOCHECK ADD CONSTRAINT ck_name CHECK (len([Name]) < 30);
        CREATE NONCLUSTERED INDEX [ix.filtered] ON [{s}].[Order Items] (qty DESC) INCLUDE ([Name]) WHERE ([Name] IS NOT NULL) WITH (FILLFACTOR = 80);
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'it''s the items', @level0type = N'SCHEMA', @level0name = N'{s}', @level1type = N'TABLE', @level1name = N'Order Items';
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'quantity', @level0type = N'SCHEMA', @level0name = N'{s}', @level1type = N'TABLE', @level1name = N'Order Items', @level2type = N'COLUMN', @level2name = N'qty';
        GO
        CREATE TRIGGER [{s}].[trg_items] ON [{s}].[Order Items] AFTER INSERT AS
        BEGIN
            SET NOCOUNT ON;
            INSERT INTO [{s}].[audit] (item_id, action) SELECT id, 'insert' FROM inserted;
        END
        GO
        CREATE TABLE dbo.[Order Items] (unrelated int);
        GO
        """;

    private static async Task Execute(string cs, string sql)
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

    private static async Task<List<string>> Rows(string cs, string sql, string table)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@t", table);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)))));
        return rows;
    }

    internal static async Task<List<string>> Definition(string cs, string table) =>
    [
        .. await Rows(cs, """
            SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name,
                   cc.definition, cc.is_persisted, dc.definition, CONVERT(nvarchar(40), ic.seed_value), CONVERT(nvarchar(40), ic.increment_value)
            FROM sys.columns c
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id
            """, table),
        .. await Rows(cs, """
            SELECT CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 OR i.name NOT LIKE 'UQ__%' THEN i.name END, i.type, i.is_unique,
                   i.is_primary_key, i.fill_factor, i.filter_definition,
                   (SELECT STRING_AGG(c.name + ':' + CONVERT(varchar, ic.is_descending_key) + ':' + CONVERT(varchar, ic.is_included_column), ',') WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id)
                    FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id)
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(@t) AND i.type > 0 ORDER BY i.type, i.is_primary_key DESC, i.name
            """, table),
        .. await Rows(cs, """
            SELECT CASE WHEN is_system_named = 1 THEN '<generated>' ELSE name END, definition, is_not_trusted, is_disabled
            FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(@t) ORDER BY definition
            """, table),
        .. await Rows(cs, """
            SELECT name, OBJECT_NAME(referenced_object_id), delete_referential_action_desc, is_not_trusted
            FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(@t) ORDER BY name
            """, table),
        .. await Rows(cs, """
            SELECT name, minor_id, CONVERT(nvarchar(max), value) FROM sys.extended_properties
            WHERE class = 1 AND major_id = OBJECT_ID(@t) ORDER BY minor_id
            """, table),
        .. await Rows(cs, "SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID(@t)", table)
    ];

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScriptedTable_RecreatesDefinitionAndBehaviorInIsolatedDatabase()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"rain.script_{suffix}";
        var database = $"rain_script_{suffix}";
        var target = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        var qualified = $"[{schema}].[Order Items]";
        try
        {
            await Execute(cs, Fixture(schema));
            var session = new DatabaseSession(new ConnectionInfo { Name = "ms", Provider = "SQL Server", ConnectionString = cs }, new SqlServerProvider());
            var result = await TableScripting.ScriptCreateAsync(session,
                new DatabaseObjectIdentity(null, schema, "Order Items", SchemaNodeType.Table));
            Assert.Null(result.Error);
            var sql = result.Sql!;
            Assert.DoesNotContain("unrelated", sql);
            Assert.Contains($"Referenced table [{schema}].[parent.t]", sql);
            Assert.Contains("GO", sql);

            await Execute(cs, $"CREATE DATABASE [{database}]");
            await Execute(target, Prerequisites(schema));
            await Execute(target, sql);
            Assert.Equal(await Definition(cs, qualified), await Definition(target, qualified));

            await Execute(target, $"INSERT INTO [{schema}].[parent.t] VALUES (1, N'p'); INSERT INTO {qualified} (parent_id, qty, [Name], code) VALUES (1, 3, N'x', N'c');");
            Assert.Equal(["100|6.00"], await Rows(target, $"SELECT id, total FROM {qualified} WHERE @t IS NOT NULL", qualified));
            Assert.Equal(["1"], await Rows(target, $"SELECT count(*) FROM [{schema}].[audit] WHERE @t IS NOT NULL", qualified));
            await Assert.ThrowsAsync<SqlException>(() => Execute(target, $"INSERT INTO {qualified} (qty, code) VALUES (0, N'c')"));
            await Assert.ThrowsAsync<SqlException>(() => Execute(target, $"INSERT INTO {qualified} (parent_id, qty, code) VALUES (99, 1, N'c')"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await Execute(cs, $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
            await Execute(cs, $"""
                DROP TABLE IF EXISTS {qualified}; DROP TABLE IF EXISTS [{schema}].[parent.t]; DROP TABLE IF EXISTS [{schema}].[audit];
                DROP TABLE IF EXISTS dbo.[Order Items];
                IF TYPE_ID(N'[{schema}].[Code]') IS NOT NULL DROP TYPE [{schema}].[Code];
                IF SCHEMA_ID(N'{schema}') IS NOT NULL DROP SCHEMA [{schema}];
                """);
        }
    }
}
