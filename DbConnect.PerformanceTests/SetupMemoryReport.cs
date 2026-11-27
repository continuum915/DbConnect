using System.Globalization;
using System.Text;

namespace DbConnect.PerformanceTests;

public static class SetupMemoryReport
{
    public static void Append(StringBuilder markdown, IReadOnlyList<RunRecord> records)
    {
        AppendTable(markdown, records, "Integrated setup memory boundaries", run => run.Setup);
        AppendTable(markdown, records.Where(run => run.Settings.ScenarioId == IntegratedRunScenario.RunId).ToArray(),
            "Run memory boundaries", run => run.RunMemory);
    }

    private static void AppendTable(StringBuilder markdown, IReadOnlyList<RunRecord> records, string title,
        Func<RunRecord, SetupMemoryObservation[]?> select)
    {
        markdown.AppendLine($"\n### {title}\n");
        markdown.AppendLine("Boundary snapshots are process-wide and include concurrent work and diagnostic overhead. Nested stages overlap; allocation deltas are not exclusive attribution. No forced GC is used. Start records are flushed before work so interrupted stages remain visible in failed-child protocol. Missing boundaries are unmeasured.\n");
        markdown.AppendLine("| Bytes / repetition | Stage | Boundary | Child ms | Working / private MiB | Managed MiB | Total allocated MiB |");
        markdown.AppendLine("|---|---|---|---:|---:|---:|---:|");
        foreach (var run in records.Where(run => IntegratedRunScenario.IsIntegrated(run.Settings.ScenarioId)))
        {
            if (select(run) is not { } samples)
            {
                markdown.AppendLine($"| {run.Settings.SizeBytes} / {run.Repetition} | unmeasured | — | — | — | — | — |");
                continue;
            }
            foreach (var sample in samples)
            {
                string MiB(long bytes) => (bytes / 1_048_576d).ToString("0.##", CultureInfo.InvariantCulture);
                markdown.AppendLine(FormattableString.Invariant(
                    $"| {run.Settings.SizeBytes} / {run.Repetition} | {sample.Stage} | {sample.Boundary} | {sample.ElapsedMilliseconds:0.###} | {MiB(sample.WorkingSetBytes)} / {MiB(sample.PrivateBytes)} | {MiB(sample.ManagedHeapBytes)} | {MiB(sample.TotalAllocatedBytes)} |"));
            }
        }
    }
}
