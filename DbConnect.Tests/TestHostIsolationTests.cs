using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class TestHostIsolationTests
{
    [Fact]
    public async Task OrdinaryHostsUseFreshStorage_AndLeaveInheritedProfilesUntouched()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"rain-host-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var roots = new List<string>();
            foreach (var inherited in new[] { false, true })
            {
                var reportDirectory = Path.Combine(outputDirectory, inherited ? "inherited" : "unset");
                Directory.CreateDirectory(reportDirectory);
                var profile = Path.Combine(reportDirectory, "profile");
                Directory.CreateDirectory(profile);
                await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), "{\"WindowWidth\":1777}");
                var startInfo = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("vstest");
                startInfo.ArgumentList.Add(typeof(TestHostIsolationTests).Assembly.Location);
                startInfo.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName~DbConnect.Tests.OrdinaryTestHostProbe");
                startInfo.Environment.Remove("DBCONNECT_PERSISTENCE_PROBE");
                startInfo.Environment.Remove("DBCONNECT_PERSISTENCE_PROBE_DIRECTORY");
                startInfo.Environment.Remove("DBCONNECT_TEST_RUN_DIRECTORY");
                startInfo.Environment.Remove("DBCONNECT_DATA_DIRECTORY");
                if (inherited)
                    startInfo.Environment["DBCONNECT_DATA_DIRECTORY"] = profile;
                startInfo.Environment["DBCONNECT_TEST_HOST_PROBE"] = reportDirectory;
                using var process = Process.Start(startInfo)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
                var root = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(Path.Combine(reportDirectory, "plain.json")))!;
                Assert.Equal(root, JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(Path.Combine(reportDirectory, "headless.json"))));
                Assert.False(Directory.Exists(root), $"Test host left its directory behind: {root}");
                roots.Add(root);
                Assert.Equal("{\"WindowWidth\":1777}", await File.ReadAllTextAsync(Path.Combine(profile, "settings.json")));
                Assert.Single(Directory.GetFiles(profile, "*", SearchOption.AllDirectories));
            }
            Assert.NotEqual(roots[0], roots[1]);
        }
        finally { Directory.Delete(outputDirectory, recursive: true); }
    }
}

public class OrdinaryTestHostProbe
{
    [Fact]
    public async Task PlainTest_DefaultServicesUseFreshTestStorage()
    {
        var reportDirectory = Environment.GetEnvironmentVariable("DBCONNECT_TEST_HOST_PROBE");
        if (reportDirectory == null) return;
        var root = AppDataPaths.RootDirectory;
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "rain-tests") + Path.DirectorySeparatorChar, root);
        Assert.Equal(root, Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY"));
        Assert.Equal(1200, (await new SettingsService().LoadAsync()).WindowWidth);
        var session = new SessionPersistenceService();
        await session.SaveAsync(new SavedSession { Tabs = [new SavedTab { QueryText = "SELECT isolated_test_host" }] });
        Assert.Equal("SELECT isolated_test_host", Assert.Single((await session.LoadAsync())!.Tabs).QueryText);
        var recovery = new SessionRecoveryService();
        await recovery.WriteSnapshotAsync(new SessionSnapshot { Pid = int.MaxValue, Timestamp = DateTimeOffset.UtcNow });
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Assert.Single(await recovery.ReadOrphanedSnapshotsAsync()).FilePath);
        await recovery.DeleteCurrentSnapshotAsync();
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "plain.json"), JsonSerializer.Serialize(root));
    }

    [AvaloniaFact]
    public async Task HeadlessTest_AppResourcesUseTheSameIsolatedStorage()
    {
        var reportDirectory = Environment.GetEnvironmentVariable("DBCONNECT_TEST_HOST_PROBE");
        if (reportDirectory == null) return;
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "rain-tests") + Path.DirectorySeparatorChar, AppDataPaths.RootDirectory);
        Assert.Equal(Path.Combine(AppDataPaths.RootDirectory, "diagnostics"), DiagnosticsService.Default.RootDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "headless.json"), JsonSerializer.Serialize(AppDataPaths.RootDirectory));
    }
}
