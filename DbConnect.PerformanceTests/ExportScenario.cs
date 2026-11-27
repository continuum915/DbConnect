using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

/// <summary>Manual streaming validation; reports memory samples without imposing flaky test thresholds.</summary>
internal sealed class ExportScenario : IExportSource, IExportPathPicker, IProgress<ExportProgress>
{
    private readonly long _count;
    private readonly string _path;
    private long _peakManaged;
    private readonly string _payload = new('x', 128);
    public IReadOnlyList<ExportColumn> Columns { get; } = [new("id", 0), new("value", 1)];

    private ExportScenario(long count, string path) { _count = count; _path = path; }

    public static async Task<int> RunAsync(long count, ExportFormat format = ExportFormat.Csv)
    {
        var extension = format switch { ExportFormat.JsonLines => "jsonl", ExportFormat.Excel => "xlsx", _ => "csv" };
        var path = Path.Combine(Path.GetTempPath(), $"rain-large-{Guid.NewGuid():N}.{extension}");
        var scenario = new ExportScenario(count, path);
        try
        {
            var start = GC.GetTotalMemory(true);
            var outcome = await new ResultExporter(scenario).ExportAsync(scenario, new() { Format = format }, ExportScope.DisplayedQueryResults, progress: scenario);
            Console.WriteLine($"status={outcome.Status}; rows={outcome.RowsWritten}; bytes={new FileInfo(path).Length}; seconds={outcome.Elapsed.TotalSeconds:F2}; managed_start={start}; managed_peak_sample={scenario._peakManaged}; managed_end_after_gc={GC.GetTotalMemory(true)}; process_peak_working_set={Process.GetCurrentProcess().PeakWorkingSet64}");
            return outcome.Status == ExportStatus.Completed && outcome.RowsWritten == count ? 0 : 1;
        }
        finally { File.Delete(path); }
    }

    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken token)
    {
        for (long i = 0; i < _count; i++)
        {
            token.ThrowIfCancellationRequested();
            yield return [i, _payload];
        }
        await Task.CompletedTask;
    }
    public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(_path);
    public void Report(ExportProgress progress)
    {
        if (progress.RowsWritten % 10000 == 0) _peakManaged = Math.Max(_peakManaged, GC.GetTotalMemory(false));
    }
}
