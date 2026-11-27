using System.Runtime.CompilerServices;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Isolates every ordinary host before any test can freeze application storage paths.</summary>
internal static class TestDataBootstrap
{
    internal static string? DirectoryPath { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        var probe = Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE");
        if (probe != null)
        {
            var probeDirectory = Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE_DIRECTORY");
            if (probe is not ("isolated" or "default" or "invalid") ||
                probeDirectory == null || !Path.IsPathFullyQualified(probeDirectory) || !Directory.Exists(probeDirectory))
                throw new InvalidOperationException("Persistence probes require a supervised directory and a known mode.");
            if (probe == "isolated")
            {
                DirectoryPath = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
                if (DirectoryPath == null || !Path.IsPathFullyQualified(DirectoryPath) ||
                    Path.GetFullPath(DirectoryPath) != Path.GetFullPath(probeDirectory))
                    throw new InvalidOperationException("The isolated probe must use its supervisor-assigned directory.");
            }
            else
            {
                var assigned = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
                if ((probe == "default" && assigned != null) ||
                    (probe == "invalid" && (assigned == null || Path.IsPathFullyQualified(assigned))))
                    throw new InvalidOperationException("The directory-resolution probe has an unexpected override.");
            }
            return; // Default/invalid modes run only the existing read-only directory-resolution probe.
        }

        var assignedRun = Environment.GetEnvironmentVariable("DBCONNECT_TEST_RUN_DIRECTORY");
        var run = assignedRun == null ? TestRunDirectory.Create() : TestRunDirectory.Validate(assignedRun);
        DirectoryPath = Path.Combine(run, $"host-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, ".rain-test-host"), "rain-test-host-v1");
        Environment.SetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY", DirectoryPath);
        Console.WriteLine($"DbConnect test data: {DirectoryPath}");
        if (assignedRun == null)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup(run);
    }

    internal static void EnsureInitialized()
    {
        if (DirectoryPath == null || AppDataPaths.RootDirectory != DirectoryPath)
            throw new InvalidOperationException("Headless application startup requires isolated test storage.");
    }

    private static void Cleanup(string run)
    {
        try { TestRunDirectory.Delete(run); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DbConnect test cleanup failed for {run}: {exception.GetType().Name}. Temporary data was retained.");
        }
    }
}
