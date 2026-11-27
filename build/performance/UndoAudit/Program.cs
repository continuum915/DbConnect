using System.Text.Json;

namespace UndoAudit;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args[0] == "--child") await StressChild.RunAsync(int.Parse(args[1]), int.Parse(args[2]), args[3]);
            else if (args[0] == "--analyze") TraceSummary.Reanalyze(args[1]);
            else if (args[0] == "--parent") await Supervisor.RunAsync(args[1], args[2], args[3], int.Parse(args[4]), int.Parse(args[5]), args[6]);
            else throw new ArgumentException("Expected --child or --parent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static void Send(string phase, object? data = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { phase, data }));
        Console.Out.Flush();
    }
}

public sealed record StepRecord(int Cycle, string Operation, bool Cleanup, long EditStart, long EditEnd,
    double EditMilliseconds, long EditAllocatedBytes, int[] EditCollections, long ConsumerStart, long ConsumerEnd,
    double ConsumerMilliseconds, long ConsumerAllocatedBytes, int[] ConsumerCollections, double TotalMilliseconds,
    double RenderMilliseconds, bool Validated);
public sealed record Suspension(long Start, long End, string Reason);
