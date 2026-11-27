using System.Diagnostics;

namespace DbConnect.PerformanceTests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--compare")
        {
            try { await ComparisonCommand.RunAsync(args); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        }
        if (args.Length >= 1 && args[0] == "--generate-scripts")
        {
            try { await ScriptFixtureWriter.RunAsync(args); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        }
        if (args.Length == 2 && args[0] == "--excel-export")
            return await ExportScenario.RunAsync(long.Parse(args[1]), DbConnect.Models.ExportFormat.Excel);
        if (args.Length == 2 && args[0] == "--jsonl-export")
            return await ExportScenario.RunAsync(long.Parse(args[1]), DbConnect.Models.ExportFormat.JsonLines);
        if (args.Length == 2 && args[0] == "--csv-export")
            return await ExportScenario.RunAsync(long.Parse(args[1]));
#if DEBUG
        if (Environment.GetEnvironmentVariable("DBCONNECT_PERFORMANCE_ALLOW_DEBUG") != "1")
        {
            Console.Error.WriteLine("Build and run the harness in Release configuration.");
            return 2;
        }
#endif
        if (Debugger.IsAttached)
            return 2;
        if (args.Length == 4 && args[0] == "--child")
            return await ChildScenario.RunAsync(ScenarioSettings.ParseChild(args));
        try
        {
            if (args.Length is 2 or 3 && args[0] == "--editor-edits")
            {
                if (args.Length == 3 && args[2] != "--smoke") throw new ArgumentException("Expected --smoke.");
                await EditorEditBenchmark.RunAsync(args[1], args.Length == 3);
                Console.WriteLine(Path.GetFullPath(args[1]));
                return 0;
            }
            var options = RunnerOptions.Parse(args);
            var records = new List<RunRecord>();
            var supervisor = new ProcessSupervisor();
            foreach (var settings in options.Expand())
            {
                for (var repetition = 0; repetition <= options.Repetitions; repetition++)
                    records.Add(await supervisor.RunAsync(settings, repetition, repetition == 0,
                        TimeSpan.FromSeconds(options.TimeoutSeconds), options.SamplingMilliseconds));
            }
            await ReportWriter.WriteAsync(options.Output, records, options);
            Console.WriteLine(Path.GetFullPath(options.Output));
            return records.All(run => run.Outcome == "completed") ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
