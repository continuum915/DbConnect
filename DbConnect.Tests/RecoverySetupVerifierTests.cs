using System.Text;
using System.Text.Json;
using DbConnect.Models;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public class RecoverySetupVerifierTests
{
    [Fact]
    public async Task ChecksLegacyAndStreamedSnapshotsIncludingEscapedAndUnicodeSql()
    {
        const string sql = "SELECT 'O''Brien', \"name\", '\\';\r\n\t<>& café 😀 \u0001";
        var snapshot = Snapshot(sql);
        using var legacy = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(RecoverySetupVerifier.Matches(legacy, sql));

        await using var streamed = new MemoryStream();
        await RecoverySnapshotWriter.WriteAsync(streamed, snapshot);
        streamed.Position = 0;
        Assert.True(RecoverySetupVerifier.Matches(streamed, sql));
    }

    [Fact]
    public async Task ValidatesLargeSqlWithBoundedAllocation()
    {
        var sql = new string('x', 2 * 1_048_576) + "\r\n'😀'";
        await using var source = new MemoryStream();
        await RecoverySnapshotWriter.WriteAsync(source, Snapshot(sql));
        source.Position = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(RecoverySetupVerifier.Matches(source, sql));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 128 * 1024, $"Readback allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData("late-sql")]
    [InlineData("extra-tab")]
    [InlineData("wrong-pid")]
    [InlineData("truncated")]
    [InlineData("bad-escape")]
    [InlineData("extra-data")]
    [InlineData("invalid-timestamp")]
    [InlineData("invalid-guid")]
    public void RejectsCorruptionWithoutWeakeningContentChecks(string corruption)
    {
        const string sql = "SELECT @value;\r\n";
        var snapshot = Snapshot(sql);
        if (corruption == "late-sql") snapshot.Tabs[0].QueryText = sql[..^1] + "!";
        if (corruption == "extra-tab") snapshot.Tabs.Add(new TabSnapshot());
        if (corruption == "wrong-pid") snapshot.Pid = int.MaxValue;
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        if (corruption == "truncated") json = json[..^8];
        if (corruption == "bad-escape") json = json.Replace("SELECT", "\\qSELECT", StringComparison.Ordinal);
        if (corruption == "extra-data") json += " null";
        if (corruption is "invalid-timestamp" or "invalid-guid")
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            if (corruption == "invalid-timestamp") node["Timestamp"] = "Monday";
            else node["Tabs"]![0]!["ConnectionId"] = "not-a-guid";
            json = node.ToJsonString();
        }
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.False(RecoverySetupVerifier.Matches(source, sql));
    }

    private static SessionSnapshot Snapshot(string sql) => new()
    {
        Pid = Environment.ProcessId,
        Timestamp = DateTimeOffset.UtcNow,
        Tabs = [new TabSnapshot { Title = "Setup", QueryText = sql, SessionName = null, ConnectionId = Guid.NewGuid() }],
        ActiveTabTitle = "Setup"
    };
}
