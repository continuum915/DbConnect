using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// Tests for Epic L (Provider Depth) — multi-schema tree, lazy folders, VirtualTable detection,
/// SQLite system-table filtering, PRAGMA nodes, FK folders, included indexes, and ScriptPragma VM helper.
/// </summary>
public class EpicLProviderTests : MainWindowTestBase
{
    private static string CreateSharedInMemoryConnectionString() =>
        $"Data Source=DbConnectTestsEpicL_{System.Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // ── SQLite: system tables are filtered ────────────────────────────────────

    [Fact]
    public async Task SqliteGetSchemaAsync_ExcludesInternalSqliteTables()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        // sqlite_sequence gets created automatically when AUTOINCREMENT is used
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT);
            """);

        var roots = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(roots);
        var tableNames = root.Children
            .Where(n => n.Type == SchemaNodeType.Table || n.Type == SchemaNodeType.VirtualTable)
            .Select(n => n.RawName)
            .ToList();

        Assert.Contains("Orders", tableNames);
        Assert.DoesNotContain("sqlite_sequence", tableNames);
    }

    [Fact]
    public async Task SqliteGetSchemaAsync_NoChildrenStartWithSqliteUnderscore()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY AUTOINCREMENT, Total REAL);
            """);

        var roots = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(roots);

        // No table/virtual table node should start with "sqlite_" regardless of how AUTOINCREMENT is used
        var internalNodes = root.Children
            .Where(n => n.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable)
            .Where(n => n.RawName?.StartsWith("sqlite_", System.StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Empty(internalNodes);
    }

    // ── SQLite: VirtualTable detection ────────────────────────────────────────

    [Fact]
    public async Task SqliteGetSchemaAsync_DetectsVirtualTablesAndExtractsModule()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE VIRTUAL TABLE UsersSearch USING fts5(Name, content=Users, content_rowid=Id);
            """);

        var roots = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(roots);

        var vt = Assert.Single(root.Children, n => n.Type == SchemaNodeType.VirtualTable);
        Assert.Equal("UsersSearch", vt.RawName);
        Assert.Equal("fts5", vt.DataType);
    }

    // ── SQLite: VirtualTable script produces stored DDL, not circular ─────────

    [Fact]
    public async Task SqliteGetViewDefinitionAsync_VirtualTable_ReturnsDdlNotCircular()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE VIRTUAL TABLE Docs USING fts5(title, body);
            """);

        var ddl = await provider.GetViewDefinitionAsync(cs, "Docs");

        Assert.NotNull(ddl);
        Assert.StartsWith("CREATE VIRTUAL TABLE", ddl, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fts5", ddl);
        // Must NOT be a circular CREATE VIEW ... AS SELECT * FROM Docs
        Assert.DoesNotContain("SELECT * FROM", ddl, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── SQLite: DatabaseInfoFolder exists + PRAGMA nodes load ─────────────────

    [Fact]
    public async Task SqliteGetSchemaAsync_HasDatabaseInfoFolder()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        var roots = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(roots);

        Assert.Single(root.Children, n => n.Type == SchemaNodeType.DatabaseInfoFolder);
    }

    [Fact]
    public async Task SqliteGetObjectsAsync_DatabaseInfoFolder_ReturnsPragmaNodes()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        var nodes = await provider.GetObjectsAsync(cs, "", SchemaNodeType.DatabaseInfoItem);

        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(SchemaNodeType.DatabaseInfoItem, n.Type));
        // foreign_keys pragma must be present
        Assert.Contains(nodes, n => n.RawName == "foreign_keys");
    }

    // ── SQLite: ForeignKeyFolder + FK nodes ───────────────────────────────────

    [Fact]
    public async Task SqliteGetSchemaAsync_TableWithForeignKey_HasForeignKeyFolder()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Categories (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, CategoryId INTEGER REFERENCES Categories(Id));
            """);

        var roots = await provider.GetSchemaAsync(cs);
        var root = Assert.Single(roots);

        var productsNode = Assert.Single(root.Children, n => n.RawName == "Products");
        Assert.Contains(productsNode.Children, n => n.Type == SchemaNodeType.ForeignKeyFolder);
    }

    [Fact]
    public async Task SqliteGetObjectsAsync_ForeignKeyFolder_ReturnsFkNodes()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Categories (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, CategoryId INTEGER REFERENCES Categories(Id));
            """);

        var fkNodes = await provider.GetObjectsAsync(cs, "Products", SchemaNodeType.ForeignKey);

        Assert.NotEmpty(fkNodes);
        Assert.All(fkNodes, n => Assert.Equal(SchemaNodeType.ForeignKey, n.Type));
        Assert.Contains(fkNodes, n => n.Name.Contains("Categories"));
    }

    // ── SQLite: index IncludedColumns field (not populated for SQLite, verify it stays empty) ──

    [Fact]
    public async Task SqliteGetIndexesAsync_RegularIndex_HasNoIncludedColumns()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT, Email TEXT);
            CREATE INDEX IX_Customers_Name ON Customers(Name);
            """);

        var indexes = await provider.GetIndexesAsync(cs, "Customers");
        var idx = Assert.Single(indexes);
        Assert.Equal("IX_Customers_Name", idx.Name);
        Assert.Empty(idx.IncludedColumns);
    }

    // ── IndexDefinition: IncludedColumns model test ───────────────────────────

    [Fact]
    public void IndexDefinition_IncludedColumns_IsInitialized()
    {
        var idx = new IndexDefinition { Name = "IX", TableName = "T" };
        Assert.NotNull(idx.IncludedColumns);
        idx.Columns.Add("col1");
        idx.IncludedColumns.Add("col2");
        Assert.Single(idx.Columns);
        Assert.Single(idx.IncludedColumns);
    }

    // ── ColumnDefinition: IsIdentity model test ───────────────────────────────

    [Fact]
    public void ColumnDefinition_IsIdentity_DefaultsFalse()
    {
        var col = new ColumnDefinition { Name = "Id", DataType = "int" };
        Assert.False(col.IsIdentity);
    }

    [Fact]
    public void ColumnDefinition_Clone_PreservesIsIdentity()
    {
        var col = new ColumnDefinition { Name = "Id", DataType = "int", IdentityKind = TableDesignIdentityKind.Identity };
        var clone = col.Clone();
        Assert.True(clone.IsIdentity);
    }

    // ── SchemaContextAction: ScriptPragma is defined ──────────────────────────

    [Fact]
    public void SchemaContextAction_ContainsScriptPragma()
    {
        var values = System.Enum.GetValues<SchemaContextAction>();
        Assert.Contains(SchemaContextAction.ScriptPragma, values);
    }

    // ── SQLite: view definition still works after GetViewDefinitionAsync change ─

    [Fact]
    public async Task SqliteGetViewDefinitionAsync_View_ReturnsCreateViewSql()
    {
        var cs = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE VIEW ActiveCustomers AS SELECT * FROM Customers WHERE Id > 0;
            """);

        var ddl = await provider.GetViewDefinitionAsync(cs, "ActiveCustomers");

        Assert.NotNull(ddl);
        Assert.StartsWith("CREATE VIEW", ddl, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ActiveCustomers", ddl);
    }

    // ── PostgreSQL provider capability: ExtensionFolder / RoleFolder types exist ─

    [Fact]
    public void SchemaNodeType_ExtensionAndRoleTypesExist()
    {
        var values = System.Enum.GetValues<SchemaNodeType>().ToHashSet();
        Assert.Contains(SchemaNodeType.Extension, values);
        Assert.Contains(SchemaNodeType.Role, values);
        Assert.Contains(SchemaNodeType.ExtensionFolder, values);
        Assert.Contains(SchemaNodeType.RoleFolder, values);
    }

    // ── ScriptPragma availability from MainWindowViewModel ───────────────────

    [Fact]
    public void GetSchemaActionAvailability_ScriptPragma_EnabledForDatabaseInfoItem()
    {
        var provider = new SqliteProvider();
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "t", Provider = "SQLite", ConnectionString = "Data Source=:memory:" },
            provider);

        var vm = CreateMainWindowViewModel(new DbConnect.Services.DatabaseService());

        var rootNode = new SchemaNode { Name = "Main", RawName = "Main", Type = SchemaNodeType.Database };
        var node = new SchemaNode
        {
            Name = "foreign_keys = 0",
            RawName = "foreign_keys",
            Type = SchemaNodeType.DatabaseInfoItem
        };
        rootNode.Children.Add(node);
        vm.ActiveSessions[rootNode] = session;

        var avail = vm.GetSchemaActionAvailability(node, SchemaContextAction.ScriptPragma);
        Assert.True(avail.IsVisible);
        Assert.True(avail.IsEnabled);
    }

    [Fact]
    public void GetSchemaActionAvailability_ScriptPragma_NotVisibleForTableNode()
    {
        var provider = new SqliteProvider();
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "t", Provider = "SQLite", ConnectionString = "Data Source=:memory:" },
            provider);

        var vm = CreateMainWindowViewModel(new DbConnect.Services.DatabaseService());

        var rootNode = new SchemaNode { Name = "Main", RawName = "Main", Type = SchemaNodeType.Database };
        var node = new SchemaNode
        {
            Name = "Customers",
            RawName = "Customers",
            Type = SchemaNodeType.Table
        };
        rootNode.Children.Add(node);
        vm.ActiveSessions[rootNode] = session;

        var avail = vm.GetSchemaActionAvailability(node, SchemaContextAction.ScriptPragma);
        Assert.False(avail.IsVisible);
    }
}
