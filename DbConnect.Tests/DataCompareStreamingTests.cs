using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class DataCompareStreamingTests
{
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

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"rain_compare_{Guid.NewGuid():N}.db");

    private static async Task<DatabaseSession> CreateFileSessionAsync(string path, params string[] setupSql)
    {
        var session = CreateSession($"Data Source={path}");
        foreach (var sql in setupSql)
            await session.ExecuteNonQueryAsync(sql);

        return session;
    }

    private static async Task<List<RowDiff>> CompareAllAsync(DataCompareService svc, DataCompareRequest request)
    {
        var diffs = new List<RowDiff>();
        await foreach (var chunk in svc.CompareAsync(request))
            diffs.AddRange(chunk.Differences);

        return diffs;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    [Fact]
    public void DataCompareChunk_EmptyDifferences_IsEmpty()
    {
        var chunk = new DataCompareChunk();
        Assert.Empty(chunk.Differences);
        Assert.False(chunk.IsLast);
    }

    [Fact]
    public void DataSyncProgress_DefaultValues_AreZero()
    {
        var progress = new DataSyncProgress();
        Assert.Equal(0, progress.RowsCompared);
        Assert.Equal(0, progress.DifferencesFound);
        Assert.False(progress.IsTruncated);
    }

    [Fact]
    public async Task GenerateSyncScript_OnlyInSource_GeneratesInsert()
    {
        var svc = new DataCompareService();
        var diffs = new List<RowDiff>
        {
            new()
            {
                Kind = RowDiffKind.OnlyInSource,
                SourceRow = new() { ["id"] = 1, ["name"] = "Alice" },
                PrimaryKeyValue = "1"
            }
        };
        var scripts = new List<string>();
        await foreach (var s in svc.GenerateSyncScriptAsync(diffs, "users", new List<string> { "id" }, new SqliteProvider().Capabilities))
            scripts.Add(s);
        Assert.Contains(scripts, s => s.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateSyncScript_OnlyInTarget_GeneratesDelete()
    {
        var svc = new DataCompareService();
        var diffs = new List<RowDiff>
        {
            new()
            {
                Kind = RowDiffKind.OnlyInTarget,
                TargetRow = new() { ["id"] = 99 },
                PrimaryKeyValue = "99"
            }
        };
        var scripts = new List<string>();
        await foreach (var s in svc.GenerateSyncScriptAsync(diffs, "users", new List<string> { "id" }, new SqliteProvider().Capabilities))
            scripts.Add(s);
        Assert.Contains(scripts, s => s.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateSyncScript_BatchesAtThousand()
    {
        var svc = new DataCompareService();
        var diffs = Enumerable.Range(1, 1001).Select(i => new RowDiff
        {
            Kind = RowDiffKind.OnlyInSource,
            SourceRow = new() { ["id"] = i },
            PrimaryKeyValue = i.ToString()
        }).ToList();
        var scripts = new List<string>();
        await foreach (var s in svc.GenerateSyncScriptAsync(diffs, "t", new List<string> { "id" }, new SqliteProvider().Capabilities, batchSize: 1000))
            scripts.Add(s);
        var begins = scripts.Count(s => s.StartsWith("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase));
        Assert.True(begins >= 2, $"Expected at least 2 BEGIN TRANSACTION blocks, got {begins}");
    }

    [Fact]
    public void RowDiff_ModifiedKind_CanBeCreated()
    {
        var diff = new RowDiff { Kind = RowDiffKind.Modified };
        Assert.Equal(RowDiffKind.Modified, diff.Kind);
    }

    [Fact]
    public void DataCompareRequest_DefaultMaxRows_Is100k()
    {
        var req = new DataCompareRequest();
        Assert.Equal(100_000, req.MaxRows);
    }

    [Fact]
    public void DataCompareRequest_DefaultPolicy_IsSourceWins()
    {
        var req = new DataCompareRequest();
        Assert.Equal(ConflictResolutionPolicy.SourceWins, req.ConflictPolicy);
    }

    [Fact]
    public async Task CompareAsync_IdenticalRows_ReturnsEqualDiffsOnly()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var svc = new DataCompareService();

            var diffs = await CompareAllAsync(svc, new DataCompareRequest
            {
                SourceSession = src,
                TargetSession = tgt,
                SourceTable = "Items",
                TargetTable = "Items",
                PrimaryKeyColumns = new List<string> { "Id" }
            });

            Assert.Equal(2, diffs.Count);
            Assert.All(diffs, diff => Assert.Equal(RowDiffKind.Equal, diff.Kind));
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task CompareAsync_BinaryDifference_DetectsModifiedRow()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Files (Id INTEGER PRIMARY KEY, Payload BLOB)",
                "INSERT INTO Files VALUES (1, X'0102')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Files (Id INTEGER PRIMARY KEY, Payload BLOB)",
                "INSERT INTO Files VALUES (1, X'0103')");
            var svc = new DataCompareService();

            var diffs = await CompareAllAsync(svc, new DataCompareRequest
            {
                SourceSession = src,
                TargetSession = tgt,
                SourceTable = "Files",
                TargetTable = "Files",
                PrimaryKeyColumns = new List<string> { "Id" }
            });

            var modified = Assert.Single(diffs, diff => diff.Kind == RowDiffKind.Modified);
            Assert.Equal("1", modified.PrimaryKeyValue);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task CompareAsync_MaxRowsExceeded_ReportsTruncation()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')",
                "INSERT INTO Items VALUES (3, 'C')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')",
                "INSERT INTO Items VALUES (3, 'C')");
            var svc = new DataCompareService();
            DataCompareChunk? finalChunk = null;

            await foreach (var chunk in svc.CompareAsync(new DataCompareRequest
            {
                SourceSession = src,
                TargetSession = tgt,
                SourceTable = "Items",
                TargetTable = "Items",
                PrimaryKeyColumns = new List<string> { "Id" },
                MaxRows = 2
            }))
            {
                finalChunk = chunk;
            }

            Assert.NotNull(finalChunk);
            Assert.True(finalChunk!.Progress.IsTruncated);
            Assert.Equal(2, finalChunk.Progress.RowsCompared);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    [Fact]
    public async Task CompareAsync_WhereFilter_LimitsComparedRows()
    {
        var p1 = TempDbPath();
        var p2 = TempDbPath();
        try
        {
            var src = await CreateFileSessionAsync(p1,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'A')",
                "INSERT INTO Items VALUES (2, 'B')");
            var tgt = await CreateFileSessionAsync(p2,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT)",
                "INSERT INTO Items VALUES (1, 'X')",
                "INSERT INTO Items VALUES (2, 'B')");
            var svc = new DataCompareService();

            var diffs = await CompareAllAsync(svc, new DataCompareRequest
            {
                SourceSession = src,
                TargetSession = tgt,
                SourceTable = "Items",
                TargetTable = "Items",
                PrimaryKeyColumns = new List<string> { "Id" },
                WhereFilter = "Id = 2"
            });

            Assert.Single(diffs);
            Assert.Equal(RowDiffKind.Equal, diffs[0].Kind);
            Assert.Equal("2", diffs[0].PrimaryKeyValue);
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }

    private static async Task<string> ScriptForAsync(DatabaseProviderCapabilities targetDialect, RowDiff diff)
    {
        var lines = new List<string>();
        await foreach (var line in new DataCompareService().GenerateSyncScriptAsync([diff], "dbo.users", ["id"], targetDialect))
            lines.Add(line);
        return Assert.Single(lines, line => !line.Contains("TRANSACTION") && line != "COMMIT;");
    }

    private static readonly RowDiff ModifiedUser = new()
    {
        Kind = RowDiffKind.Modified,
        SourceRow = new() { ["id"] = 1, ["payload"] = new byte[] { 0xAB, 0x01 }, ["active"] = true },
        PrimaryKeyValue = "1"
    };

    [Fact]
    public async Task GenerateSyncScript_WritesSqlServerIdentifiersAndLiterals_ForASqlServerTarget()
    {
        Assert.Equal(
            "UPDATE [dbo].[users] SET [payload] = 0xAB01, [active] = 1 WHERE [id] = 1;",
            await ScriptForAsync(new SqlServerProvider().Capabilities, ModifiedUser));
    }

    [Fact]
    public async Task GenerateSyncScript_WritesByteaAndBooleanKeywords_ForAPostgreSqlTarget()
    {
        Assert.Equal(
            """UPDATE "dbo"."users" SET "payload" = '\xAB01'::bytea, "active" = TRUE WHERE "id" = 1;""",
            await ScriptForAsync(new PostgreSqlProvider().Capabilities, ModifiedUser));
    }

    [Fact]
    public async Task GenerateSyncScript_QuotesIdentifiersWithBackticks_ForAMySqlTarget()
    {
        Assert.Equal(
            "UPDATE `dbo`.`users` SET `payload` = X'AB01', `active` = 1 WHERE `id` = 1;",
            await ScriptForAsync(new MySqlProvider().Capabilities, ModifiedUser));
    }

    [Fact]
    public async Task GenerateSyncScript_OpensEachBatchWithTheTargetsBeginStatement()
    {
        var diffs = Enumerable.Range(1, 3).Select(i => new RowDiff
        {
            Kind = RowDiffKind.OnlyInSource,
            SourceRow = new() { ["id"] = i },
            PrimaryKeyValue = i.ToString()
        });
        var lines = new List<string>();
        await foreach (var line in new DataCompareService().GenerateSyncScriptAsync(diffs, "t", ["id"], new MySqlProvider().Capabilities, batchSize: 2))
            lines.Add(line);

        Assert.Equal(2, lines.Count(line => line == "START TRANSACTION;"));
        Assert.DoesNotContain("BEGIN TRANSACTION;", lines);
        Assert.Equal("COMMIT;", lines[^1]);
    }

    [Theory]
    [InlineData("'\\x01\\x02'::BLOB", 0)]
    [InlineData("'\\x01\\x03'::BLOB", 1)]
    public async Task CompareAsync_DuckDbBlobs_CompareByContent(string targetPayload, int expectedModified)
    {
        var p1 = TempDbPath() + ".duckdb";
        var p2 = TempDbPath() + ".duckdb";
        try
        {
            async Task<DatabaseSession> CreateDuckDbAsync(string path, string payload)
            {
                var session = new DatabaseSession(new ConnectionInfo { Name = "Test", Provider = "DuckDB", ConnectionString = $"Data Source={path}" }, new DuckDbProvider());
                await session.ExecuteNonQueryAsync("CREATE TABLE files (id INTEGER PRIMARY KEY, payload BLOB)");
                await session.ExecuteNonQueryAsync($"INSERT INTO files VALUES (1, {payload})");
                return session;
            }

            var diffs = await CompareAllAsync(new DataCompareService(), new DataCompareRequest
            {
                SourceSession = await CreateDuckDbAsync(p1, "'\\x01\\x02'::BLOB"),
                TargetSession = await CreateDuckDbAsync(p2, targetPayload),
                SourceTable = "files",
                TargetTable = "files",
                PrimaryKeyColumns = ["id"]
            });

            Assert.Equal(expectedModified, diffs.Count(diff => diff.Kind == RowDiffKind.Modified));
            var payload = Assert.Single(diffs).SourceRow!["payload"];
            Assert.Equal<IEnumerable<byte>>(new byte[] { 0x01, 0x02 }, Assert.IsType<byte[]>(payload));
        }
        finally
        {
            TryDelete(p1);
            TryDelete(p2);
        }
    }
}
