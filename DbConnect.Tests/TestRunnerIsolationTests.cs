using System.Diagnostics;
using DbConnect.Services;

namespace DbConnect.Tests;

public class TestRunnerIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisedRunnerCleansOwnedData_AndPreservesTestExitCode(bool failTest)
    {
        var report = Path.Combine(Path.GetTempPath(), $"rain-runner-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(report);
        try
        {
            var repository = FindRepository();
            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "build", "run-tests.ps1"),
                         "-NoBuild", "-NoRestore", "-Filter", "FullyQualifiedName~DbConnect.Tests.RunnerCleanupProbe" })
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["DBCONNECT_TEST_RUNNER_REPORT"] = report;
            startInfo.Environment["DBCONNECT_TEST_RUNNER_FAIL"] = failTest ? "1" : "0";
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.Equal(failTest ? 1 : 0, process.ExitCode);
            Assert.True(File.Exists(Path.Combine(report, "root.txt")), $"{await stdout}\n{await stderr}");
            var root = await File.ReadAllTextAsync(Path.Combine(report, "root.txt"));
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "rain-tests") + Path.DirectorySeparatorChar, root);
            Assert.False(Directory.Exists(Path.GetDirectoryName(root)), $"Runner left test artifacts at {root}.");
        }
        finally { Directory.Delete(report, recursive: true); }
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DbConnect.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find the repository for the supervised-runner probe.");
    }
}

public class RunnerCleanupProbe : MainWindowTestBase
{
    [Fact]
    public async Task TestRunWritesOnlyOwnedData()
    {
        var report = Environment.GetEnvironmentVariable("DBCONNECT_TEST_RUNNER_REPORT");
        if (report == null) return;
        var viewModel = CreateMainWindowViewModel();
        viewModel.NewQueryTabWithSession().QueryText = "SELECT runner_cleanup";
        await new SettingsService().SaveAsync(new DbConnect.Models.AppSettings { WindowWidth = 1301, WindowX = 0, WindowY = 0 });
        Assert.Equal(1301, (await new SettingsService().LoadAsync()).WindowWidth);
        await File.WriteAllTextAsync(Path.Combine(report, "root.txt"), AppDataPaths.RootDirectory);
        Assert.False(Environment.GetEnvironmentVariable("DBCONNECT_TEST_RUNNER_FAIL") == "1", "Intentional supervised-runner failure.");
    }
}
