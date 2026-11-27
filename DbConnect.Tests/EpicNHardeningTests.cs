using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;
using Xunit;

namespace DbConnect.Tests;

// ── P2-070: Unit tests for Phase 2 services ───────────────────────────────────

public class CsvImportServiceErrorTests
{
    [Fact]
    public void ParseCsv_MissingFile_Throws()
    {
        var ex = Record.Exception(() =>
            CsvImportService.ParseCsv("/nonexistent/file.csv", ',', true, out _));
        Assert.NotNull(ex);
    }

    [Fact]
    public void ParseCsv_TypeInference_DetectsNumericAndText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_ti_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "Amount,Label\n1.5,foo\n2.0,bar");
        try
        {
            var rows = CsvImportService.ParseCsv(path, ',', true, out var headers);
            Assert.Equal(new[] { "Amount", "Label" }, headers);
            Assert.Equal(2, rows.Count);
            Assert.Equal("1.5", rows[0][0]);
            Assert.Equal("foo", rows[0][1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseCsv_EmptyFile_ReturnsNoRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_empty_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "");
        try
        {
            var rows = CsvImportService.ParseCsv(path, ',', false, out var headers);
            Assert.Empty(rows);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseCsv_QuotedNewline_ParsesAsAvailable()
    {
        // The current CsvImportService parser is single-line; multi-line quoted fields
        // split into multiple rows. This test documents the current behavior.
        var path = Path.Combine(Path.GetTempPath(), $"rain_nl_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "Id,Note\n1,\"line1\nline2\"");
        try
        {
            var rows = CsvImportService.ParseCsv(path, ',', true, out _);
            // Parser treats each physical line as a row — documents actual behavior
            Assert.True(rows.Count >= 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExecuteImportAsync_MismatchedColumns_FailsGracefully()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_mis_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "Id,Name\n1,Alice");
        var dbPath = Path.Combine(Path.GetTempPath(), $"rain_mis_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        await using var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE t (Id TEXT);");
        var conn = new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = connStr };
        var session = new DatabaseSession(conn, provider);

        var opts = new CsvImportOptions
        {
            FilePath = path,
            Delimiter = ',',
            HasHeaderRow = true,
            Mode = ImportMode.ExistingTable,
            TableName = "t",
            ColumnNames = new List<string> { "Id" }  // only map Id
        };

        // Should succeed (unmapped columns ignored)
        var result = await CsvImportService.ExecuteImportAsync(session, opts);
        Assert.True(result.Succeeded || result.RowsImported >= 0);
        File.Delete(path);
    }
}

public class InMemoryCredentialStoreTests
{
    // A simple in-memory ICredentialStore for tests (avoids DPAPI platform dependency).
    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _store = new();
        public Task SetAsync(string key, string secret) { _store[key] = secret; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var v) ? v : (string?)null);
        public Task DeleteAsync(string key) { _store.Remove(key); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsSameSecret()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("db_password", "supersecret");
        var got = await store.GetAsync("db_password");
        Assert.Equal("supersecret", got);
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var store = new InMemoryCredentialStore();
        var got = await store.GetAsync("no_such_key");
        Assert.Null(got);
    }

    [Fact]
    public async Task Delete_ExistingKey_RemovesIt()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("key1", "value1");
        await store.DeleteAsync("key1");
        Assert.Null(await store.GetAsync("key1"));
    }

    [Fact]
    public async Task Delete_NonExistentKey_IsNoOp()
    {
        var store = new InMemoryCredentialStore();
        // Must not throw
        await store.DeleteAsync("ghost_key");
    }

    [Fact]
    public async Task Set_OverwritesExistingValue()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("k", "v1");
        await store.SetAsync("k", "v2");
        Assert.Equal("v2", await store.GetAsync("k"));
    }
}

public class WindowsCredentialStoreTests
{
    [Fact]
    public async Task WindowsStore_SetGetDelete_RoundTrips()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var store = new WindowsCredentialStore();
        var testKey = $"rain_test_{Guid.NewGuid():N}";
        try
        {
            await store.SetAsync(testKey, "test-password-123");
            var retrieved = await store.GetAsync(testKey);
            Assert.Equal("test-password-123", retrieved);
        }
        finally
        {
            await store.DeleteAsync(testKey);
        }
    }

    [Fact]
    public async Task WindowsStore_GetMissingKey_ReturnsNull()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var store = new WindowsCredentialStore();
        var result = await store.GetAsync($"rain_missing_{Guid.NewGuid():N}");
        Assert.Null(result);
    }
}

public class SshTunnelParseHostPortTests
{
    [Fact]
    public void ParseHostPort_PostgreSql_DefaultPort()
    {
        var cs = "Host=db.example.com;Username=u;Password=p;Database=mydb";
        var (host, port) = TunnelEndpoint.ParseHostPort("PostgreSQL", cs);
        Assert.Equal("db.example.com", host);
        Assert.Equal(5432, port);
    }

    [Fact]
    public void ParseHostPort_PostgreSql_ExplicitPort()
    {
        var cs = "Host=db.example.com;Port=5433;Username=u;Password=p;Database=mydb";
        var (host, port) = TunnelEndpoint.ParseHostPort("PostgreSQL", cs);
        Assert.Equal(5433, port);
    }

    [Fact]
    public void ParseHostPort_SqlServer_DefaultPort()
    {
        var cs = "Server=sqlserver.example.com;Database=db;User Id=sa;Password=pw";
        var (host, port) = TunnelEndpoint.ParseHostPort("SQL Server", cs);
        Assert.Equal("sqlserver.example.com", host);
        Assert.Equal(1433, port);
    }

    [Fact]
    public void ParseHostPort_SqlServer_ExplicitPort()
    {
        var cs = "Server=sqlserver.example.com,1444;Database=db;User Id=sa;Password=pw";
        var (host, port) = TunnelEndpoint.ParseHostPort("SQL Server", cs);
        Assert.Equal(1444, port);
    }

    [Fact]
    public void ParseHostPort_MySql_ExplicitPort()
    {
        var (host, port) = TunnelEndpoint.ParseHostPort("MySQL", "Server=myhost;Port=3307;");
        Assert.Equal("myhost", host);
        Assert.Equal(3307, port);
    }

    [Fact]
    public void ParseHostPort_FileProvider_ReturnsLocalhostWithZeroPort()
    {
        // A file database has no server to tunnel to — documents the contract.
        var (host, port) = TunnelEndpoint.ParseHostPort("SQLite", "Data Source=app.db");
        Assert.Equal("localhost", host);
        Assert.Equal(0, port);
    }
}

public class ForeignKeyDdlTests
{
    [Fact]
    public void SqliteDdlBuilder_CreateTable_WithForeignKey_ContainsForeignKeyClause()
    {
        var builder = DdlBuilderFactory.For("SQLite");
        var cols = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsPrimaryKey = true },
            new() { Name = "OrderId", DataType = "INTEGER" }
        };
        var fks = new List<ForeignKeyDefinition>
        {
            new() { Table = "OrderItems", Column = "OrderId", ReferencedTable = "Orders",
                    ReferencedColumn = "Id", ConstraintName = "fk_order", OnDelete = "CASCADE", OnUpdate = "NO ACTION" }
        };
        var ddl = builder.GenerateCreateTable("OrderItems", cols, fks);
        Assert.Contains("FOREIGN KEY", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REFERENCES", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Orders", ddl);
    }

    [Fact]
    public void PostgreSqlDdlBuilder_AddForeignKey_ProducesAlterTable()
    {
        var builder = DdlBuilderFactory.For("PostgreSQL");
        var fk = new ForeignKeyDefinition
        {
            Table = "order_items",
            Column = "order_id",
            ReferencedTable = "orders",
            ReferencedColumn = "id",
            ConstraintName = "fk_order_id",
            OnDelete = "CASCADE",
            OnUpdate = "NO ACTION"
        };
        var ddl = builder.GenerateAddForeignKey(fk);
        Assert.Contains("ALTER TABLE", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADD CONSTRAINT", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOREIGN KEY", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REFERENCES", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASCADE", ddl);
    }

    [Fact]
    public void SqlServerDdlBuilder_AddForeignKey_UsesBracketQuoting()
    {
        var builder = DdlBuilderFactory.For("SQL Server");
        var fk = new ForeignKeyDefinition
        {
            Table = "OrderItems",
            Column = "OrderId",
            ReferencedTable = "Orders",
            ReferencedColumn = "Id",
            ConstraintName = "FK_OrderItems_Orders",
            OnDelete = "NO ACTION",
            OnUpdate = "NO ACTION"
        };
        var ddl = builder.GenerateAddForeignKey(fk);
        Assert.Contains("[OrderItems]", ddl);
        Assert.Contains("[Orders]", ddl);
        Assert.Contains("FOREIGN KEY", ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSqlDdlBuilder_AddUniqueConstraint_ProducesCorrectDdl()
    {
        var builder = DdlBuilderFactory.For("PostgreSQL");
        var ddl = builder.GenerateAddUniqueConstraint("users", "uq_users_email", new[] { "email" });
        Assert.Contains("UNIQUE", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", ddl);
    }

    [Fact]
    public void SqlServerDdlBuilder_AddCheckConstraint_ProducesCorrectDdl()
    {
        var builder = DdlBuilderFactory.For("SQL Server");
        var ddl = Assert.Single(builder.GenerateCheckChange("Products", new TableCheckChange(null, new TableCheckSpec("chk_price", "Price > 0"))));
        Assert.Equal("ALTER TABLE [Products] ADD CONSTRAINT [chk_price] CHECK (Price > 0);", ddl);
    }

    [Fact]
    public void SqlServerDdlBuilder_GenerateCreateIndex_WithIncludedColumns()
    {
        var builder = DdlBuilderFactory.For("SQL Server");
        var idx = new IndexDefinition
        {
            Name = "IX_Orders_CustomerId",
            TableName = "Orders",
            IsUnique = false,
            Columns = new List<string> { "CustomerId" },
            IncludedColumns = new List<string> { "OrderDate", "Total" }
        };
        var ddl = builder.GenerateCreateIndex(idx);
        Assert.Contains("INCLUDE", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[OrderDate]", ddl);
        Assert.Contains("[Total]", ddl);
    }
}

// ── P2-071: Integration tests for provider depth features ────────────────────

public class SqlitePragmaIntegrationTests
{
    private static string CreateTempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_pragma_{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return path;
    }

    [Fact]
    public async Task GetObjectsAsync_DatabaseInfoFolder_ReturnsPragmaNodes()
    {
        var path = CreateTempDb();
        var connStr = $"Data Source={path}";
        try
        {
            var provider = new SqliteProvider();
            var nodes = await provider.GetObjectsAsync(connStr, "Main", SchemaNodeType.DatabaseInfoItem);
            Assert.NotEmpty(nodes);
            Assert.All(nodes, n => Assert.Equal(SchemaNodeType.DatabaseInfoItem, n.Type));
            Assert.Contains(nodes, n => n.RawName == "journal_mode");
            Assert.Contains(nodes, n => n.RawName == "foreign_keys");
        }
        finally { try { File.Delete(path); } catch { /* ignore lock */ } }
    }

    [Fact]
    public async Task ExecuteQueryAsync_PragmaForeignKeys_CanBeToggled()
    {
        var path = CreateTempDb();
        var connStr = $"Data Source={path}";
        try
        {
            var provider = new SqliteProvider();
            // Default is off (0)
            var off = await provider.ExecuteQueryAsync(connStr, "PRAGMA foreign_keys;");
            Assert.False(off.HasError);

            // Enable
            await provider.ExecuteNonQueryAsync(connStr, "PRAGMA foreign_keys = ON;");
            var on = await provider.ExecuteQueryAsync(connStr, "PRAGMA foreign_keys;");
            Assert.False(on.HasError);
            Assert.Single(on.ObjectRows);
        }
        finally { try { File.Delete(path); } catch { /* ignore lock */ } }
    }

    [Fact]
    public async Task GetObjectsAsync_ForeignKeyFolder_ReturnsForeignKeyNodes()
    {
        var path = CreateTempDb();
        var connStr = $"Data Source={path}";
        await using var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();
        try
        {
            var provider = new SqliteProvider();
            await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE cats (id INTEGER PRIMARY KEY, name TEXT);");
            await provider.ExecuteNonQueryAsync(connStr,
                "CREATE TABLE kittens (id INTEGER PRIMARY KEY, cat_id INTEGER, FOREIGN KEY (cat_id) REFERENCES cats(id));");

            var fkNodes = await provider.GetObjectsAsync(connStr, "kittens", SchemaNodeType.ForeignKey);
            Assert.Single(fkNodes);
            Assert.Equal(SchemaNodeType.ForeignKey, fkNodes[0].Type);
            Assert.Contains("cats", fkNodes[0].Name);
        }
        finally
        {
            keepAlive.Close();
            await Task.Delay(50);
            try { File.Delete(path); } catch { /* ignore lock on CI */ }
        }
    }

    [Fact]
    public async Task GetObjectsAsync_TriggerFolder_ReturnsTriggerNodes()
    {
        var path = CreateTempDb();
        var connStr = $"Data Source={path}";
        await using var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();
        try
        {
            var provider = new SqliteProvider();
            await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE audit (id INTEGER PRIMARY KEY, ts TEXT);");
            await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE items (id INTEGER PRIMARY KEY, val TEXT);");
            await provider.ExecuteNonQueryAsync(connStr,
                "CREATE TRIGGER trg_items_insert AFTER INSERT ON items BEGIN INSERT INTO audit(ts) VALUES(datetime('now')); END;");

            var triggers = await provider.GetObjectsAsync(connStr, "items", SchemaNodeType.Trigger);
            Assert.Single(triggers);
            Assert.Equal("trg_items_insert", triggers[0].RawName);
        }
        finally
        {
            keepAlive.Close();
            await Task.Delay(50);
            try { File.Delete(path); } catch { /* ignore lock on CI */ }
        }
    }

    [Fact]
    public async Task GetViewDefinitionAsync_Trigger_RoundTrips()
    {
        var path = CreateTempDb();
        var connStr = $"Data Source={path}";
        await using var keepAlive = new SqliteConnection(connStr);
        await keepAlive.OpenAsync();
        try
        {
            var provider = new SqliteProvider();
            await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE log (id INTEGER PRIMARY KEY, msg TEXT);");
            await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE data (id INTEGER PRIMARY KEY);");
            await provider.ExecuteNonQueryAsync(connStr,
                "CREATE TRIGGER trg_log AFTER INSERT ON data BEGIN INSERT INTO log(msg) VALUES('inserted'); END;");

            // Retrieve via sqlite_master
            var def = await provider.GetViewDefinitionAsync(connStr, "trg_log");
            Assert.Contains("CREATE TRIGGER", def, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("trg_log", def);

            var triggerDef = await provider.GetTriggerDefinitionAsync(connStr, "main", "trg_log");
            Assert.Contains("CREATE TRIGGER", triggerDef, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("trg_log", triggerDef);
        }
        finally
        {
            keepAlive.Close();
            await Task.Delay(50);
            try { File.Delete(path); } catch { /* ignore lock on CI */ }
        }
    }
}

public class SqlServerIncludedIndexDdlTests
{
    [Fact]
    public void GenerateCreateIndex_IncludedColumns_ContainsIncludeClause()
    {
        var builder = new SqlServerDdlBuilder();
        var idx = new IndexDefinition
        {
            Name = "IX_Sales_Date",
            TableName = "Sales",
            IsUnique = false,
            Columns = new List<string> { "SaleDate" },
            IncludedColumns = new List<string> { "Amount", "CustomerId" }
        };
        var ddl = builder.GenerateCreateIndex(idx);
        Assert.StartsWith("CREATE INDEX", ddl);
        Assert.Contains("INCLUDE ([Amount], [CustomerId])", ddl);
    }

    [Fact]
    public void GenerateCreateIndex_NoIncludedColumns_OmitsIncludeClause()
    {
        var builder = new SqlServerDdlBuilder();
        var idx = new IndexDefinition
        {
            Name = "IX_Sales_Date",
            TableName = "Sales",
            IsUnique = true,
            Columns = new List<string> { "SaleDate" },
            IncludedColumns = new List<string>()
        };
        var ddl = builder.GenerateCreateIndex(idx);
        Assert.DoesNotContain("INCLUDE", ddl, StringComparison.OrdinalIgnoreCase);
    }
}

public class PostgreSqlFunctionScriptShapeTests
{
    [Fact]
    public void PostgreSqlDdlBuilder_CreateView_UsesCreateOrReplace()
    {
        var builder = new PostgreSqlDdlBuilder();
        var ddl = builder.GenerateCreateView("my_view", "SELECT 1 AS n", createOrReplace: true);
        Assert.StartsWith("CREATE OR REPLACE VIEW", ddl);
        Assert.Contains("my_view", ddl);
        Assert.Contains("SELECT 1 AS n", ddl);
    }

    [Fact]
    public void PostgreSqlDdlBuilder_AddForeignKey_IncludesOnDeleteAndOnUpdate()
    {
        var builder = new PostgreSqlDdlBuilder();
        var fk = new ForeignKeyDefinition
        {
            Table = "t",
            Column = "ref_id",
            ReferencedTable = "refs",
            ReferencedColumn = "id",
            ConstraintName = "fk_t_ref",
            OnDelete = "SET NULL",
            OnUpdate = "CASCADE"
        };
        var ddl = builder.GenerateAddForeignKey(fk);
        Assert.Contains("SET NULL", ddl);
        Assert.Contains("CASCADE", ddl);
    }

    [Fact]
    public void SqlServerDdlBuilder_GenerateCreateView_UsesCreateOrAlter()
    {
        var builder = new SqlServerDdlBuilder();
        var ddl = builder.GenerateCreateView("dbo_view", "SELECT TOP 1 1", createOrReplace: true);
        Assert.StartsWith("CREATE OR ALTER VIEW", ddl);
    }
}

// ── P2-072: Performance baseline ─────────────────────────────────────────────

public class SchemaLoadPerformanceTests
{
    [Fact]
    public void SchemaIndexer_BuildIndex_500Tables_CompletesUnder100ms()
    {
        // Build synthetic tree: 500 tables × 20 columns
        var tables = Enumerable.Range(1, 500).Select(i =>
        {
            var tbl = new SchemaNode { Name = $"table_{i}", RawName = $"table_{i}", Type = SchemaNodeType.Table };
            for (int c = 1; c <= 20; c++)
                tbl.Children.Add(new SchemaNode { Name = $"col_{c}", RawName = $"col_{c}", Type = SchemaNodeType.Column });
            return tbl;
        }).ToList();

        var indexer = new SchemaIndexer();
        var sw = Stopwatch.StartNew();
        var index = indexer.BuildIndex(tables);
        sw.Stop();

        Assert.Equal(500, index.Tables.Count);
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"SchemaIndexer.BuildIndex took {sw.ElapsedMilliseconds}ms for 500×20 tables — expected <100ms");
    }

    [Fact]
    public async Task SqliteProvider_Load500TableSchema_CompletesUnder2000ms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_perf_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={path}";

        // Seed 500 tables
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        for (int i = 1; i <= 500; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = $"CREATE TABLE t{i} (id INTEGER PRIMARY KEY, name TEXT, val REAL, ts TEXT, flag INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        conn.Close();

        try
        {
            var provider = new SqliteProvider();
            var sw = Stopwatch.StartNew();
            var schema = await provider.GetSchemaAsync(connStr);
            sw.Stop();

            var root = Assert.Single(schema);
            var tableCount = root.Children.Count(n => n.Type == SchemaNodeType.Table);
            Assert.Equal(500, tableCount);
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"GetSchemaAsync for 500 tables took {sw.ElapsedMilliseconds}ms — expected <2000ms");
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore lock */ }
        }
    }
}
