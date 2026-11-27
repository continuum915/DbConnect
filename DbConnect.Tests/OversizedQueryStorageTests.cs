using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class OversizedQueryStorageTests : IDisposable
{
    private static readonly string Oversized = new('x', StoredQueryText.CharacterLimit + 1);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-oversized-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionInfo _connection = new() { Name = "Oversized" };

    private string WorkspacePath => Path.Combine(_directory, "query-workspace.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ScriptsWithinTheLimitAreKeptWhole()
    {
        var sql = new string('x', StoredQueryText.CharacterLimit);

        var stored = StoredQueryText.From(sql);

        Assert.Same(sql, stored.Text);
        Assert.False(stored.IsTruncated);
        Assert.Null(stored.Sha256);
    }

    [Fact]
    public void OversizedScriptsKeepAPreviewLengthAndHash()
    {
        var stored = StoredQueryText.From(Oversized);

        Assert.True(stored.IsTruncated);
        Assert.Equal(StoredQueryText.PreviewCharacters, stored.Text.Length);
        Assert.Equal(Oversized.Length, stored.FullLength);
        Assert.Equal(64, stored.Sha256!.Length);
    }

    [Fact]
    public void PreviewDoesNotSplitASurrogatePair()
    {
        var sql = new string('x', StoredQueryText.PreviewCharacters - 1) + "\U0001F600" + new string('x', StoredQueryText.CharacterLimit);

        Assert.False(char.IsHighSurrogate(StoredQueryText.From(sql).Text[^1]));
    }

    [Fact]
    public async Task HistoryStoresOversizedScriptsAsPreviewsAndKeepsDifferentScriptsApart()
    {
        var workspace = new QueryWorkspaceService(WorkspacePath);
        var other = Oversized[..^1] + "y";

        await workspace.AddHistoryEntryAsync(_connection, new QueryHistoryEntry { Sql = Oversized });
        await workspace.AddHistoryEntryAsync(_connection, new QueryHistoryEntry { Sql = other });
        await workspace.AddHistoryEntryAsync(_connection, new QueryHistoryEntry { Sql = Oversized });

        var history = await workspace.LoadHistoryAsync(_connection);
        Assert.Equal(2, history.Count);
        Assert.All(history, entry =>
        {
            Assert.True(entry.IsTruncated);
            Assert.Equal(Oversized.Length, entry.FullLength);
            Assert.Equal(StoredQueryText.PreviewCharacters, entry.Sql.Length);
        });
        Assert.NotEqual(history[0].Sha256, history[1].Sha256);
        Assert.InRange(new FileInfo(WorkspacePath).Length, 0, 64 * 1024);
    }

    [Fact]
    public async Task AddingHistoryShrinksOversizedEntriesAlreadyInTheFile()
    {
        Directory.CreateDirectory(_directory);
        var workspace = new QueryWorkspaceService(WorkspacePath);
        await workspace.AddHistoryEntryAsync(_connection, new QueryHistoryEntry { Sql = "SELECT 1" });
        var state = await File.ReadAllTextAsync(WorkspacePath);
        await File.WriteAllTextAsync(WorkspacePath, state.Replace("SELECT 1", Oversized));

        await workspace.AddHistoryEntryAsync(_connection, new QueryHistoryEntry { Sql = "SELECT 2" });

        var history = await workspace.LoadHistoryAsync(_connection);
        Assert.Contains(history, entry => entry.Sql == "SELECT 2" && !entry.IsTruncated);
        Assert.Contains(history, entry => entry.IsTruncated && entry.FullLength == Oversized.Length);
        Assert.InRange(new FileInfo(WorkspacePath).Length, 0, 64 * 1024);
    }

    [Fact]
    public void ATruncatedHistoryEntryCannotReplaceTheEditorText()
    {
        var tab = new QueryTabViewModel { QueryText = "SELECT current" };
        tab.SelectedHistoryEntry = new QueryHistoryEntry { Sql = "SELECT partial", IsTruncated = true, FullLength = 300_000 };

        Assert.False(tab.LoadSelectedHistoryCommand.CanExecute(null));
        tab.LoadSelectedHistoryCommand.Execute(null);

        Assert.Equal("SELECT current", tab.QueryText);
        Assert.Contains("preview only", tab.SelectedHistoryEntry.DisplayName);
    }

    [Fact]
    public void AWholeHistoryEntryStillLoads()
    {
        var tab = new QueryTabViewModel { QueryText = "SELECT current" };
        tab.SelectedHistoryEntry = new QueryHistoryEntry { Sql = "SELECT saved" };

        Assert.True(tab.LoadSelectedHistoryCommand.CanExecute(null));
        tab.LoadSelectedHistoryCommand.Execute(null);

        Assert.Equal("SELECT saved", tab.QueryText);
    }

    [Fact]
    public async Task SlowQueryLogStoresAPreviewAndGroupsAnOversizedScriptByItsHash()
    {
        var log = new SlowQueryLogService();
        var name = "oversized-" + Guid.NewGuid().ToString("N");
        try
        {
            await log.LogExecutedAsync(name, TimeSpan.FromSeconds(2), 1, false, Oversized);
            await log.LogExecutedAsync(name, TimeSpan.FromSeconds(3), 1, false, Oversized);

            var entries = await log.LoadEntriesAsync(name);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry =>
            {
                Assert.True(entry.IsTruncated);
                Assert.Equal(StoredQueryText.PreviewCharacters, entry.QueryText.Length);
                Assert.StartsWith("oversized:", entry.Fingerprint);
            });
            var aggregate = Assert.Single(await log.GetAggregatesAsync(name));
            Assert.True(aggregate.IsTruncated);
        }
        finally { await log.ClearAsync(name); }
    }

    [Fact]
    public async Task SlowQueryLogKeepsOrdinaryQueriesAndTheirFingerprints()
    {
        var log = new SlowQueryLogService();
        var name = "ordinary-" + Guid.NewGuid().ToString("N");
        try
        {
            await log.LogExecutedAsync(name, TimeSpan.FromSeconds(2), 1, false, "SELECT * FROM t WHERE id = 42");

            var entry = Assert.Single(await log.LoadEntriesAsync(name));
            Assert.False(entry.IsTruncated);
            Assert.Equal("SELECT * FROM t WHERE id = 42", entry.QueryText);
            Assert.Equal(log.Fingerprint(entry.QueryText), entry.Fingerprint);
        }
        finally { await log.ClearAsync(name); }
    }

    [Fact]
    public void ATruncatedSlowQuerySampleCannotOpenAPlan()
    {
        var dashboard = new SlowQueryDashboardViewModel([]);
        string? opened = null;
        dashboard.RequestOpenInTab = sql => opened = sql;

        dashboard.SelectedAggregate = new SlowQueryAggregate { SampleQuery = "SELECT partial", IsTruncated = true };
        Assert.False(dashboard.ViewPlanCommand.CanExecute(null));
        dashboard.ViewPlanCommand.Execute(null);
        Assert.Null(opened);

        dashboard.SelectedAggregate = new SlowQueryAggregate { SampleQuery = "SELECT whole" };
        Assert.True(dashboard.ViewPlanCommand.CanExecute(null));
        dashboard.ViewPlanCommand.Execute(null);
        Assert.Equal("EXPLAIN\nSELECT whole", opened);
    }

    [Fact]
    public void EntriesSavedBeforeThisFormatLoadAsWhole()
    {
        var entry = JsonSerializer.Deserialize<QueryHistoryEntry>("""{"Sql":"SELECT 1"}""")!;

        Assert.False(entry.IsTruncated);
        Assert.Null(entry.Sha256);
    }
}
