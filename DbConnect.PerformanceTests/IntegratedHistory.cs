using System.Collections.ObjectModel;
using System.Diagnostics;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.PerformanceTests;

public sealed record IntegratedHistoryObservations(long WorkspaceBytes, long SlowQueryLogBytes, double LaterWorkspaceSaveMilliseconds);

/// <summary>Configures a tab as the app does, against the isolated profile, and checks what Run persisted.</summary>
internal static class IntegratedHistory
{
    // Large enough for a fixture of one command to pass the one-second slow-query threshold. It is not waited for.
    private static readonly TimeSpan ReportedStatementDuration = TimeSpan.FromSeconds(1);

    public static QueryTabViewModel CreateTab(DatabaseSession session, ImmediateDbSession fake)
    {
        fake.ReportedStatementDuration = ReportedStatementDuration;
        return new QueryTabViewModel("Integrated measurement", new ObservableCollection<DatabaseSession> { session },
            new QueryWorkspaceService(), SlowQueryLogService.Default);
    }

    public static async Task<IntegratedHistoryObservations> ValidateAndMeasureAsync(string isolated, DatabaseSession session, string sql)
    {
        var workspace = new QueryWorkspaceService();
        var history = await workspace.LoadHistoryAsync(session.Connection);
        var expected = StoredQueryText.From(sql);
        if (history.Count != 1 || history[0].Sql != expected.Text || history[0].IsTruncated != expected.IsTruncated ||
            history[0].Sha256 != expected.Sha256)
            throw new InvalidDataException("Run did not record its SQL in workspace history.");

        var rowCap = await workspace.LoadResultRowCapAsync();
        var save = Stopwatch.StartNew();
        await workspace.SaveResultRowCapAsync(rowCap);
        var laterSave = save.Elapsed.TotalMilliseconds;

        return new(new FileInfo(Path.Combine(isolated, "query-workspace.json")).Length,
            await WaitForSlowQueryLogAsync(isolated, expected.Text.Length), laterSave);
    }

    // Production appends without awaiting; the parent deadline bounds this wait.
    private static async Task<long> WaitForSlowQueryLogAsync(string isolated, int minimumBytes)
    {
        while (true)
        {
            var size = Directory.GetFiles(isolated, "slow-queries-*.jsonl").Sum(path => new FileInfo(path).Length);
            if (size >= minimumBytes) return size;
            await Task.Delay(50);
        }
    }
}
