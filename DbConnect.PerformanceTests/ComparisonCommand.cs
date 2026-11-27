using System.Text.Json;

namespace DbConnect.PerformanceTests;

public static class ComparisonCommand
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length is not (3 or 5) || args[0] != "--compare" || (args.Length == 5 && args[3] != "--output"))
            throw new ArgumentException("Usage: --compare baseline.json candidate.json [--output directory]");
        var output = args.Length == 5 ? args[4] : Path.Combine("artifacts", "performance", "comparison-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        foreach (var name in new[] { "comparison.json", "comparison.md" })
            foreach (var input in args.Skip(1).Take(2))
                if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(Path.Combine(output, name)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("Comparison output must not overwrite either input report.");
        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
        using var candidate = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
        var comparison = ReportComparer.Compare(baseline.RootElement, candidate.RootElement);
        await ComparisonReportWriter.WriteAsync(output, comparison);
        Console.WriteLine(Path.GetFullPath(output));
    }
}
