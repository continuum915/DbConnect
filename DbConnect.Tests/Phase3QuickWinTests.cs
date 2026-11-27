using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

// ── P3: Phase 3 Quick Win Tests ───────────────────────────────────────────────

#region SnippetServiceTests

public class SnippetServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SnippetService _svc;

    public SnippetServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"rain_snippets_{Guid.NewGuid():N}");
        _svc = new SnippetService(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Fact]
    public async Task SaveAndLoad_GlobalSnippet_RoundTrip()
    {
        var snippet = new Snippet
        {
            Name = "Test Global",
            Description = "desc",
            Body = "SELECT 1",
            Scope = SnippetScope.Global,
            Tags = new List<string> { "test" }
        };

        await _svc.SaveAsync(snippet);
        var loaded = await _svc.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(snippet.Id, loaded[0].Id);
        Assert.Equal(snippet.Name, loaded[0].Name);
        Assert.Equal(snippet.Body, loaded[0].Body);
        Assert.Equal(SnippetScope.Global, loaded[0].Scope);
    }

    [Fact]
    public async Task SaveScopedSnippet_Load_PreservesScopeFields()
    {
        var snippet = new Snippet
        {
            Name = "Conn Snippet",
            Body = "SELECT 2",
            Scope = SnippetScope.Connection,
            ConnectionName = "MyConn",
            DatabaseName = "MyDb"
        };

        await _svc.SaveAsync(snippet);
        var loaded = await _svc.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(SnippetScope.Connection, loaded[0].Scope);
        Assert.Equal("MyConn", loaded[0].ConnectionName);
    }

    [Fact]
    public async Task Delete_RemovesSnippetFromLoadedList()
    {
        var s1 = new Snippet { Name = "A", Body = "SELECT A", Scope = SnippetScope.Global };
        var s2 = new Snippet { Name = "B", Body = "SELECT B", Scope = SnippetScope.Global };
        await _svc.SaveAsync(s1);
        await _svc.SaveAsync(s2);

        await _svc.DeleteAsync(s1.Id);
        var loaded = await _svc.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal("B", loaded[0].Name);
    }

    [Fact]
    public async Task PromoteToGlobal_ChangesScopeAndClearsConnectionDatabase()
    {
        var snippet = new Snippet
        {
            Name = "Promote Me",
            Body = "SELECT 3",
            Scope = SnippetScope.Database,
            ConnectionName = "C1",
            DatabaseName = "D1"
        };
        await _svc.SaveAsync(snippet);

        await _svc.PromoteToGlobalAsync(snippet.Id);
        var loaded = await _svc.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(SnippetScope.Global, loaded[0].Scope);
        Assert.Null(loaded[0].ConnectionName);
        Assert.Null(loaded[0].DatabaseName);
    }

    [Fact]
    public async Task FilterText_MatchesName_Description_AndTags()
    {
        await _svc.SaveAsync(new Snippet { Name = "Alpha query", Body = "SELECT 1", Scope = SnippetScope.Global, Tags = new List<string> { "prod" } });
        await _svc.SaveAsync(new Snippet { Name = "Beta", Description = "alpha helper", Body = "SELECT 2", Scope = SnippetScope.Global });
        await _svc.SaveAsync(new Snippet { Name = "Gamma", Body = "SELECT 3", Scope = SnippetScope.Global, Tags = new List<string> { "alpha" } });
        await _svc.SaveAsync(new Snippet { Name = "Delta", Body = "SELECT 4", Scope = SnippetScope.Global });

        var vm = new SnippetPanelViewModel(_svc);
        await vm.LoadAsync();

        vm.FilterText = "alpha";
        var filtered = vm.FilteredSnippets.ToList();

        Assert.Equal(3, filtered.Count); // name, description, and tag all match "alpha"
    }
}

#endregion

#region SnippetPanelViewModelTests

public class SnippetPanelViewModelTests
{
    private static List<Snippet> BuildSnippets() => new()
    {
        new Snippet { Name = "Global One", Description = "first", Body = "SELECT 1", Scope = SnippetScope.Global, Tags = new List<string> { "tag1" } },
        new Snippet { Name = "Conn Two", Description = "second", Body = "SELECT 2", Scope = SnippetScope.Connection, ConnectionName = "C1" },
        new Snippet { Name = "DB Three", Body = "SELECT 3", Scope = SnippetScope.Database, ConnectionName = "C1", DatabaseName = "D1" },
    };

    [Fact]
    public void FilterTextEmpty_ShowsAllSnippets()
    {
        var vm = new SnippetPanelViewModel();
        vm.SetSnippetsForTest(BuildSnippets());
        vm.FilterText = string.Empty;

        Assert.Equal(3, vm.FilteredSnippets.Count);
    }

    [Fact]
    public void FilterText_FiltersByNameCaseInsensitive()
    {
        var vm = new SnippetPanelViewModel();
        vm.SetSnippetsForTest(BuildSnippets());

        vm.FilterText = "CONN";
        Assert.Single(vm.FilteredSnippets);
        Assert.Equal("Conn Two", vm.FilteredSnippets[0].Name);
    }

    [Fact]
    public void SelectedSnippetNull_IsPromoteEnabledFalse()
    {
        var vm = new SnippetPanelViewModel();
        vm.SetSnippetsForTest(BuildSnippets());
        vm.SelectedSnippet = null;

        Assert.False(vm.IsPromoteEnabled);
    }

    [Fact]
    public void SelectedSnippetGlobalScope_IsPromoteEnabledFalse_ScopedTrue()
    {
        var vm = new SnippetPanelViewModel();
        var snippets = BuildSnippets();
        vm.SetSnippetsForTest(snippets);

        // Global scope → false
        vm.SelectedSnippet = snippets.First(s => s.Scope == SnippetScope.Global);
        Assert.False(vm.IsPromoteEnabled);

        // Connection scope → true
        vm.SelectedSnippet = snippets.First(s => s.Scope == SnippetScope.Connection);
        Assert.True(vm.IsPromoteEnabled);
    }
}

#endregion

#region SessionRecoveryServiceTests

public class SessionRecoveryServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SessionRecoveryService _svc;

    public SessionRecoveryServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"rain_recovery_{Guid.NewGuid():N}");
        _svc = new SessionRecoveryService(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Fact]
    public async Task WriteSnapshot_CreatesFileInRecoveryDirectory()
    {
        var snapshot = new SessionSnapshot
        {
            Pid = 99999,
            Timestamp = DateTimeOffset.UtcNow,
            Tabs = new List<TabSnapshot> { new TabSnapshot { Title = "Q1", QueryText = "SELECT 1" } }
        };

        await _svc.WriteSnapshotAsync(snapshot);

        var files = Directory.GetFiles(_tmpDir, "session-*.json");
        Assert.Single(files);
    }

    [Fact]
    public async Task ReadOrphanedSnapshots_ReturnsOnlyFilesWithDeadPid()
    {
        // Write a snapshot with a PID that is definitely not running (very high PID)
        var deadSnapshot = new SessionSnapshot
        {
            Pid = 2147483647, // int.MaxValue — definitely not running
            Timestamp = DateTimeOffset.UtcNow,
            Tabs = new List<TabSnapshot> { new TabSnapshot { Title = "Old", QueryText = "SELECT old" } }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(deadSnapshot);
        Directory.CreateDirectory(_tmpDir);
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "session-2147483647-20200101120000.json"), json);

        // Write a snapshot for the current process
        var aliveSnapshot = new SessionSnapshot
        {
            Pid = Environment.ProcessId,
            Timestamp = DateTimeOffset.UtcNow,
            Tabs = new List<TabSnapshot> { new TabSnapshot { Title = "Current", QueryText = "SELECT current" } }
        };
        var json2 = System.Text.Json.JsonSerializer.Serialize(aliveSnapshot);
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, $"session-{Environment.ProcessId}-20200101120001.json"), json2);

        var orphans = await _svc.ReadOrphanedSnapshotsAsync();

        // Only the dead-PID snapshot should be returned
        Assert.Single(orphans);
        Assert.Equal(2147483647, orphans[0].Snapshot.Pid);
    }

    [Fact]
    public async Task DeleteSnapshot_RemovesFile()
    {
        var snapshot = new SessionSnapshot
        {
            Pid = 99998,
            Timestamp = DateTimeOffset.UtcNow,
            Tabs = new List<TabSnapshot> { new TabSnapshot { Title = "Q", QueryText = "SELECT 1" } }
        };
        await _svc.WriteSnapshotAsync(snapshot);
        var files = Directory.GetFiles(_tmpDir, "session-*.json");
        Assert.Single(files);

        await _svc.DeleteSnapshotAsync(files[0]);
        Assert.Empty(Directory.GetFiles(_tmpDir, "session-*.json"));
    }

    [Fact]
    public async Task TakeRecoverable_KeepsRecentSnapshotsWithText_AndDeletesTheRest()
    {
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_tmpDir);
        async Task<string> Orphan(int pid, DateTimeOffset at, string text)
        {
            var path = Path.Combine(_tmpDir, $"session-{pid}-{at:yyyyMMddHHmmss}.json");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new SessionSnapshot
            {
                Pid = pid,
                Timestamp = at,
                Tabs = [new TabSnapshot { Title = "Q", QueryText = text }]
            }));
            return path;
        }
        var recent = await Orphan(2147483640, now.AddHours(-1), "SELECT recent");
        var older = await Orphan(2147483641, now.AddDays(-2), "SELECT older");
        var expired = await Orphan(2147483642, now - SessionRecoveryService.MaxOrphanAge - TimeSpan.FromMinutes(1), "SELECT expired");
        var blank = await Orphan(2147483643, now.AddMinutes(-5), "   ");

        var taken = await _svc.TakeRecoverableSnapshotsAsync(now);

        Assert.Equal([older, recent], taken.Select(t => t.FilePath));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(blank));
    }
}

#endregion

#region GlobalSearchTests

public class GlobalSearchTests
{
    private static SchemaNode MakeNode(string name, SchemaNodeType type = SchemaNodeType.Table)
        => new SchemaNode { Name = name, RawName = name, Type = type };

    [Fact]
    public void ExactNameMatch_RanksAboveContainsMatch()
    {
        var indexer = new SchemaIndexer();
        var nodes = new List<SchemaNode>
        {
            MakeNode("order_items"),   // contains "order"
            MakeNode("orders"),        // starts with "order" — but exact match for "orders"
            MakeNode("order"),         // exact match for "order"
        };

        var connections = new List<(string, IEnumerable<SchemaNode>)>
        {
            ("conn1", nodes)
        };

        var results = indexer.SearchAll("order", connections);

        Assert.NotEmpty(results);
        Assert.Equal("order", results[0].ObjectName); // exact match first
    }

    [Fact]
    public void Search_AcrossTwoConnections_ReturnsBothResults()
    {
        var indexer = new SchemaIndexer();
        var nodes1 = new List<SchemaNode> { MakeNode("customers") };
        var nodes2 = new List<SchemaNode> { MakeNode("customers_archive") };

        var connections = new List<(string, IEnumerable<SchemaNode>)>
        {
            ("conn1", nodes1),
            ("conn2", nodes2)
        };

        var results = indexer.SearchAll("customers", connections);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ConnectionName == "conn1");
        Assert.Contains(results, r => r.ConnectionName == "conn2");
    }

    [Fact]
    public void EmptySearch_ReturnsNoResults()
    {
        var indexer = new SchemaIndexer();
        var nodes = new List<SchemaNode> { MakeNode("users"), MakeNode("orders") };
        var connections = new List<(string, IEnumerable<SchemaNode>)>
        {
            ("conn1", nodes)
        };

        var results = indexer.SearchAll(string.Empty, connections);
        Assert.Empty(results);

        var results2 = indexer.SearchAll("   ", connections);
        Assert.Empty(results2);
    }
}

#endregion
