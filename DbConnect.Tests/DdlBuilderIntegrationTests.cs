using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.Tests;

public class DdlBuilderIntegrationTests
{
    private static string CreateSharedInMemoryConnectionString() =>
        $"Data Source=DdlIntegration_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // ── SQLite EXECUTABLE ROUND-TRIPS ─────────────────────────────────────────

    [Fact]
    public async Task SqliteBuilder_CreateTable_ExecutesSuccessfully()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "TEXT", IsNullable = false }
        };
        var ddl = DdlBuilderFactory.For("SQLite").GenerateCreateTable("Products", columns);
        await provider.ExecuteNonQueryAsync(cs, ddl);

        var schema = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(schema);
        var table = Assert.Single(root.Children, n => n.Type == SchemaNodeType.Table && n.RawName == "Products");
        var columnChildren = table.Children.Where(n => n.Type == SchemaNodeType.Column).ToList();
        Assert.Equal(2, columnChildren.Count);
    }

    [Fact]
    public async Task SqliteBuilder_CreateTable_MultiColumnPk_ExecutesSuccessfully()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "TenantId", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "TEXT", IsNullable = true }
        };
        var ddl = DdlBuilderFactory.For("SQLite").GenerateCreateTable("TenantItems", columns);
        await provider.ExecuteNonQueryAsync(cs, ddl);

        var schema = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(schema);
        Assert.Single(root.Children, n => n.Type == SchemaNodeType.Table && n.RawName == "TenantItems");
    }

    [Fact]
    public async Task SqliteBuilder_AlterTable_AddColumn_PreservesDataAndAddsColumn()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Orders" ("Id" INTEGER NOT NULL PRIMARY KEY, "Total" REAL NOT NULL);
            INSERT INTO "Orders" ("Id", "Total") VALUES (1, 99.99);
            """);

        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Total", DataType = "REAL", IsNullable = false }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Total", DataType = "REAL", IsNullable = false },
            new() { Name = "Notes", DataType = "TEXT", IsNullable = true, IsNew = true }
        };

        var script = DdlBuilderFactory.For("SQLite").GenerateAlterTable("Orders", original, modified);
        await ExecuteScriptAsync(cs, script);

        var rows = await provider.ExecuteQueryAsync(cs, "SELECT Id, Total, Notes FROM \"Orders\";");
        Assert.False(rows.HasError, rows.ErrorMessage);
        var row = Assert.Single(rows.ObjectRows);
        Assert.Equal(1L, row[rows.IndexOfColumn("Id")]);
        Assert.Equal(99.99, row[rows.IndexOfColumn("Total")]);
        Assert.Null(row[rows.IndexOfColumn("Notes")]);
    }

    [Fact]
    public async Task SqliteBuilder_AlterTable_DropColumn_PreservesRemainingData()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Customers" ("Id" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Email" TEXT);
            INSERT INTO "Customers" ("Id", "Name", "Email") VALUES (1, 'Ada', 'ada@example.com');
            """);

        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "TEXT", IsNullable = false },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "TEXT", IsNullable = false },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true, IsDropped = true }
        };

        var script = DdlBuilderFactory.For("SQLite").GenerateAlterTable("Customers", original, modified);
        await ExecuteScriptAsync(cs, script);

        var rows = await provider.ExecuteQueryAsync(cs, "SELECT Id, Name FROM \"Customers\";");
        Assert.False(rows.HasError, rows.ErrorMessage);
        var row = Assert.Single(rows.ObjectRows);
        Assert.Equal(1L, row[rows.IndexOfColumn("Id")]);
        Assert.Equal("Ada", row[rows.IndexOfColumn("Name")]);
        Assert.DoesNotContain("Email", rows.ColumnInfos.Select(c => c.Name));
    }

    [Fact]
    public async Task SqliteBuilder_AlterTable_RecreatesIndexAfterRebuild()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Users" ("Id" INTEGER NOT NULL PRIMARY KEY, "Email" TEXT);
            CREATE INDEX "IX_Users_Email" ON "Users" ("Email");
            """);

        var indexes = await provider.GetIndexesAsync(cs, "Users");
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Email", DataType = "TEXT", IsNullable = false }
        };

        var script = DdlBuilderFactory.For("SQLite").GenerateAlterTable("Users", original, modified, indexes);
        await ExecuteScriptAsync(cs, script);

        var rebuiltIndexes = await provider.GetIndexesAsync(cs, "Users");
        Assert.Contains(rebuiltIndexes, idx => idx.Name == "IX_Users_Email");
    }

    [Fact]
    public async Task SqliteBuilder_CreateAndDropIndex_RoundTrip()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Products" ("Id" INTEGER NOT NULL PRIMARY KEY, "Sku" TEXT);
            """);

        var indexDef = new IndexDefinition
        {
            Name = "IX_Products_Sku",
            TableName = "Products",
            Columns = ["Sku"],
            IsUnique = true
        };

        var createSql = DdlBuilderFactory.For("SQLite").GenerateCreateIndex(indexDef);
        await provider.ExecuteNonQueryAsync(cs, createSql);

        var before = await provider.GetIndexesAsync(cs, "Products");
        Assert.Contains(before, idx => idx.Name == "IX_Products_Sku");

        var dropSql = DdlBuilderFactory.For("SQLite").GenerateDropIndex("IX_Products_Sku");
        await provider.ExecuteNonQueryAsync(cs, dropSql);

        var after = await provider.GetIndexesAsync(cs, "Products");
        Assert.DoesNotContain(after, idx => idx.Name == "IX_Products_Sku");
    }

    [Fact]
    public async Task SqliteBuilder_CreateView_ExecutesSuccessfully()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Products" ("Id" INTEGER NOT NULL PRIMARY KEY, "Price" REAL);
            INSERT INTO "Products" ("Id", "Price") VALUES (1, 150.0);
            """);

        var viewSql = DdlBuilderFactory.For("SQLite").GenerateCreateView(
            "ExpensiveProducts",
            "SELECT * FROM \"Products\" WHERE Price > 100");
        await provider.ExecuteNonQueryAsync(cs, viewSql);

        var schema = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(schema);
        Assert.Contains(root.Children, n => n.Type == SchemaNodeType.View && n.RawName == "ExpensiveProducts");
    }

    [Fact]
    public async Task SqliteBuilder_AddUniqueConstraint_ExecutesSuccessfully()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "Customers" ("Id" INTEGER NOT NULL PRIMARY KEY, "Email" TEXT);
            INSERT INTO "Customers" ("Id", "Email") VALUES (1, 'a@example.com');
            """);

        var constraintSql = DdlBuilderFactory.For("SQLite")
            .GenerateAddUniqueConstraint("Customers", "UQ_Customers_Email", ["Email"]);
        await provider.ExecuteNonQueryAsync(cs, constraintSql);

        // Duplicate email insert should throw
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await provider.ExecuteNonQueryAsync(cs,
                "INSERT INTO \"Customers\" (\"Id\", \"Email\") VALUES (2, 'a@example.com');"));
    }

    // ── POSTGRESQL TEXT-LEVEL ASSERTIONS ─────────────────────────────────────

    [Fact]
    public void PostgreSqlBuilder_AlterTable_NullableToNotNull_EmitsSetNotNull()
    {
        var original = new List<ColumnDefinition> { new() { Name = "Email", DataType = "TEXT", IsNullable = true } };
        var modified = new List<ColumnDefinition> { new() { Name = "Email", DataType = "TEXT", IsNullable = false } };
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAlterTable("Users", original, modified);
        Assert.Contains("SET NOT NULL", sql);
        Assert.DoesNotContain("TYPE", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_AlterTable_NotNullToNullable_EmitsDropNotNull()
    {
        var original = new List<ColumnDefinition> { new() { Name = "Email", DataType = "TEXT", IsNullable = false } };
        var modified = new List<ColumnDefinition> { new() { Name = "Email", DataType = "TEXT", IsNullable = true } };
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAlterTable("Users", original, modified);
        Assert.Contains("DROP NOT NULL", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_AlterTable_DefaultAdded_EmitsSetDefault()
    {
        var original = new List<ColumnDefinition> { new() { Name = "Score", DataType = "INT", IsNullable = true, DefaultValue = "" } };
        var modified = new List<ColumnDefinition> { new() { Name = "Score", DataType = "INT", IsNullable = true, DefaultValue = "0" } };
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAlterTable("Users", original, modified);
        Assert.Contains("SET DEFAULT 0", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_AlterTable_DefaultRemoved_EmitsDropDefault()
    {
        var original = new List<ColumnDefinition> { new() { Name = "Score", DataType = "INT", IsNullable = true, DefaultValue = "0" } };
        var modified = new List<ColumnDefinition> { new() { Name = "Score", DataType = "INT", IsNullable = true, DefaultValue = "" } };
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAlterTable("Users", original, modified);
        Assert.Contains("DROP DEFAULT", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_AlterTable_TypeChangeOnly_EmitsOnlyTypeStatement()
    {
        var original = new List<ColumnDefinition> { new() { Name = "Age", DataType = "INT", IsNullable = false } };
        var modified = new List<ColumnDefinition> { new() { Name = "Age", DataType = "BIGINT", IsNullable = false } };
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAlterTable("Users", original, modified);
        Assert.Contains("TYPE BIGINT", sql);
        Assert.DoesNotContain("NOT NULL", sql);
        Assert.DoesNotContain("DEFAULT", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_CreateView_CreateOrReplace_UsesCreateOrReplaceView()
    {
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateCreateView("MyView", "SELECT 1", createOrReplace: true);
        Assert.StartsWith("CREATE OR REPLACE VIEW", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_CreateView_NoReplace_UsesCreateView()
    {
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateCreateView("MyView", "SELECT 1", createOrReplace: false);
        Assert.StartsWith("CREATE VIEW", sql);
        Assert.DoesNotContain("OR REPLACE", sql);
    }

    // ── SQL SERVER TEXT-LEVEL ASSERTIONS ─────────────────────────────────────

    [Fact]
    public void SqlServerBuilder_DropIndex_WithTableName_EmitsOnClause()
    {
        var sql = DdlBuilderFactory.For("SQL Server").GenerateDropIndex("IX_Foo", "Orders");
        Assert.Equal("DROP INDEX [IX_Foo] ON [Orders];", sql);
    }

    [Fact]
    public void SqlServerBuilder_DropIndex_WithoutTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DdlBuilderFactory.For("SQL Server").GenerateDropIndex("IX_Foo"));
    }

    [Fact]
    public void SqlServerBuilder_AlterTable_AddColumn_EmitsAlterTableAdd()
    {
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Notes", DataType = "NVARCHAR(500)", IsNullable = true, IsNew = true }
        };
        var sql = DdlBuilderFactory.For("SQL Server").GenerateAlterTable("Orders", original, modified);
        Assert.Contains("ALTER TABLE [Orders] ADD [Notes]", sql);
    }

    [Fact]
    public void SqlServerBuilder_AlterTable_DropColumn_EmitsAlterTableDropColumn()
    {
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Notes", DataType = "NVARCHAR(500)", IsNullable = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Notes", DataType = "NVARCHAR(500)", IsNullable = true, IsDropped = true }
        };
        var sql = DdlBuilderFactory.For("SQL Server").GenerateAlterTable("Orders", original, modified);
        Assert.Contains("ALTER TABLE [Orders] DROP COLUMN [Notes]", sql);
    }

    [Fact]
    public void SqlServerBuilder_AlterTable_TypeChange_EmitsAlterColumn()
    {
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Code", DataType = "NVARCHAR(50)", IsNullable = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Code", DataType = "NVARCHAR(200)", IsNullable = true }
        };
        var sql = DdlBuilderFactory.For("SQL Server").GenerateAlterTable("Products", original, modified);
        Assert.Contains("ALTER TABLE [Products] ALTER COLUMN [Code]", sql);
        Assert.Contains("NVARCHAR(200)", sql);
    }

    [Fact]
    public void SqlServerBuilder_CreateView_CreateOrReplace_UsesCreateOrAlter()
    {
        var sql = DdlBuilderFactory.For("SQL Server").GenerateCreateView("MyView", "SELECT 1", createOrReplace: true);
        Assert.StartsWith("CREATE OR ALTER VIEW", sql);
        Assert.Contains("[MyView]", sql);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Run the exact script through the same provider seam used by the designer.
    private static Task<int> ExecuteScriptAsync(string connectionString, string script) =>
        new SqliteProvider().ExecuteNonQueryAsync(connectionString, script);
}
