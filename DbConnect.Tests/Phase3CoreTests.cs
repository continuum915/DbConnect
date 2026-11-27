using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Phase 3 core tests: SchemaDiff, DataCompare, BackupService, DependencyAnalysis.</summary>
public class Phase3CoreTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DatabaseSession CreateSession(string connectionString)
    {
        var provider = new SqliteProvider();
        var conn = new ConnectionInfo
        {
            Name = "Test",
            Provider = "SQLite",
            ConnectionString = connectionString
        };
        return new DatabaseSession(conn, provider);
    }

    /// <summary>Creates a unique temp file path (on Windows TEMP, not /tmp).</summary>
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"rain_test_{Guid.NewGuid():N}.db");

    private static async Task<DatabaseSession> CreateFileSessionAsync(string path,
        params string[] setupSql)
    {
        var cs = $"Data Source={path}";
        var session = CreateSession(cs);
        foreach (var sql in setupSql)
            await session.ExecuteNonQueryAsync(sql);
        return session;
    }

    // ── SchemaDiffService Tests ────────────────────────────────────────────────

    [Fact]
    public async Task SchemaDiff_TwoEmptySessions_ReturnsNoDiffItems()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1);
            var tgt = await CreateFileSessionAsync(p2);
            var svc = new SchemaDiffService();

            var result = await svc.CompareAsync(src, tgt);

            Assert.NotNull(result);
            Assert.Empty(result.Items);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task SchemaDiff_SourceHasTable_TargetDoesNot_ReturnsOnlyInSource()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT)");
            var tgt = await CreateFileSessionAsync(p2);
            var svc = new SchemaDiffService();

            var result = await svc.CompareAsync(src, tgt);

            var item = Assert.Single(result.Items);
            Assert.Equal(DiffStatus.OnlyInSource, item.Status);
            Assert.Equal("Products", item.ObjectName, ignoreCase: true);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task SchemaDiff_TargetHasTable_SourceDoesNot_ReturnsOnlyInTarget()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1);
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Total REAL)");
            var svc = new SchemaDiffService();

            var result = await svc.CompareAsync(src, tgt);

            var item = Assert.Single(result.Items);
            Assert.Equal(DiffStatus.OnlyInTarget, item.Status);
            Assert.Equal("Orders", item.ObjectName, ignoreCase: true);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task SchemaDiff_BothHaveSameTable_ReturnsInBoth()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT)");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT)");
            var svc = new SchemaDiffService();

            var result = await svc.CompareAsync(src, tgt);

            var item = Assert.Single(result.Items);
            Assert.Equal(DiffStatus.InBoth, item.Status);
            Assert.Equal("Customers", item.ObjectName, ignoreCase: true);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    // ── DataCompareService Tests ───────────────────────────────────────────────

    [Fact]
    public async Task DataCompare_NoPrimaryKey_ReturnsError()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1, "CREATE TABLE T (Name TEXT)");
            var tgt = await CreateFileSessionAsync(p2, "CREATE TABLE T (Name TEXT)");
            var svc = new DataCompareService();

            var result = await svc.CompareAsync(src, "T", tgt, "T");

            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("primary key", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task DataCompare_IdenticalData_ReturnsIdenticalRows()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var svc = new DataCompareService();

            var result = await svc.CompareAsync(src, "Items", tgt, "Items");

            Assert.Null(result.ErrorMessage);
            Assert.All(result.Rows, r => Assert.Equal(RowDiffStatus.Identical, r.Status));
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task DataCompare_SourceHasExtraRow_ReturnsOnlyInSource()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'A')");
            var svc = new DataCompareService();

            var result = await svc.CompareAsync(src, "Items", tgt, "Items");

            Assert.Null(result.ErrorMessage);
            var onlyInSrc = result.Rows.Where(r => r.Status == RowDiffStatus.OnlyInSource).ToList();
            Assert.Single(onlyInSrc);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task DataCompare_ModifiedRow_ReturnsModified()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'NewValue')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Val TEXT)",
                "INSERT INTO Items VALUES (1, 'OldValue')");
            var svc = new DataCompareService();

            var result = await svc.CompareAsync(src, "Items", tgt, "Items");

            Assert.Null(result.ErrorMessage);
            var modified = result.Rows.Where(r => r.Status == RowDiffStatus.Modified).ToList();
            Assert.Single(modified);
            Assert.Contains("Val", modified[0].ChangedColumns, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    // ── BackupService Tests ────────────────────────────────────────────────────

    [Fact]
    public async Task BackupSqlite_CreatesOutputFile()
    {
        var src = TempDbPath();
        var dst = TempDbPath();
        try
        {
            // Create source DB with some data
            await using var conn = new SqliteConnection($"Data Source={src}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE T (x INTEGER); INSERT INTO T VALUES (42);";
            await cmd.ExecuteNonQueryAsync();

            var svc = new BackupService();
            var progress = new Progress<string>(_ => { });
            await svc.BackupSqliteAsync($"Data Source={src}", dst, progress);

            Assert.True(File.Exists(dst));
            Assert.True(new FileInfo(dst).Length > 0);
        }
        finally
        {
            TryDelete(src);
            TryDelete(dst);
        }
    }

    [Fact]
    public async Task CheckPgDumpAvailable_ReturnsBool_DoesNotThrow()
    {
        var svc = new BackupService();
        var available = await svc.CheckPgDumpAvailableAsync();
        // Just verify it returns without throwing — pg_dump may or may not be installed
        Assert.IsType<bool>(available);
    }

    [Fact]
    public async Task BackupProfile_RoundTrip_SaveAndLoad()
    {
        var svc = new BackupService();
        var profile = new BackupProfile
        {
            Name = "Test Profile",
            Provider = "SQLite",
            OutputPath = @"C:\Backups\test.db"
        };

        try
        {
            await svc.SaveProfileAsync(profile);
            var profiles = await svc.LoadProfilesAsync();
            var loaded = profiles.FirstOrDefault(p => p.Id == profile.Id);

            Assert.NotNull(loaded);
            Assert.Equal("Test Profile", loaded!.Name);
            Assert.Equal("SQLite", loaded.Provider);
        }
        finally
        {
            // Clean up
            await svc.DeleteProfileAsync(profile.Id);
        }
    }

    // ── DependencyAnalysisService Tests ──────────────────────────────────────

    [Fact]
    public async Task GetDependencies_TableWithNoDeps_ReturnsEmpty()
    {
        var p1 = TempDbPath();
        try
        {
            var session = await CreateFileSessionAsync(p1,
                "CREATE TABLE Standalone (Id INTEGER PRIMARY KEY, Name TEXT)");
            var svc = new DependencyAnalysisService();

            var deps = await svc.GetDependenciesAsync(session, "Standalone", "Table");

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            TryDelete(p1);
        }
    }

    [Fact]
    public async Task GetDependencies_UnknownObject_ReturnsEmptyNoThrow()
    {
        var p1 = TempDbPath();
        try
        {
            var session = await CreateFileSessionAsync(p1);
            var svc = new DependencyAnalysisService();

            var deps = await svc.GetDependenciesAsync(session, "NonExistentTable", "Table");

            Assert.NotNull(deps);
            Assert.Empty(deps);
        }
        finally
        {
            TryDelete(p1);
        }
    }

    [Fact]
    public async Task GetDependsOn_TableWithForeignKey_ReturnsFkDependency()
    {
        var p1 = TempDbPath();
        try
        {
            var session = await CreateFileSessionAsync(p1,
                "CREATE TABLE Categories (Id INTEGER PRIMARY KEY, Name TEXT)",
                "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, CatId INTEGER REFERENCES Categories(Id))");
            var svc = new DependencyAnalysisService();

            var deps = await svc.GetDependsOnAsync(session, "Products", "Table");

            Assert.NotNull(deps);
            var fk = deps.FirstOrDefault(d => d.DependencyKind == "foreign key");
            Assert.NotNull(fk);
            Assert.Equal("Categories", fk!.DependsOnName, ignoreCase: true);
        }
        finally
        {
            TryDelete(p1);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
