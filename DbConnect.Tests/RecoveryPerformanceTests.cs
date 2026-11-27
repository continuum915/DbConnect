using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class RecoveryPerformanceTests
{
    [Fact]
    public async Task Supervisor_RecoveryRetainsValidatedMetricsAndStorageContext()
    {
        var run = await new ProcessSupervisor().RunAsync(new RunSettings(RecoveryScenario.Id, "representative", 4096),
            1, false, TimeSpan.FromSeconds(30));
        Assert.Equal("completed", run.Outcome);
        Assert.NotNull(run.Metrics);
        Assert.NotNull(run.Recovery);
        Assert.True(run.Recovery.PersistedBytes > 4096);
        Assert.False(Directory.Exists(run.Recovery.Directory));
        Assert.Equal(3, run.Metrics.CollectionsByGeneration.Length);
    }

    [Fact]
    public async Task Recovery_RoundTripsSnapshotAndCleansIsolatedDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DbConnect-recovery-test-" + Guid.NewGuid().ToString("N"));
        using (var scenario = new RecoveryScenario(RepresentativeImport.Generate(4096), directory))
        {
            await scenario.ExecuteAsync();
            var observed = await scenario.ValidateAsync();
            Assert.Equal(2, observed.TabCount);
            Assert.True(observed.PersistedBytes > 4096);
            Assert.Contains("combined", observed.Attribution);
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Recovery_PersistenceErrorIsNotSuccessfulMeasurement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DbConnect-recovery-test-" + Guid.NewGuid().ToString("N"));
        using var scenario = new RecoveryScenario(RepresentativeImport.Generate(4096), directory);
        Directory.Delete(directory);
        await File.WriteAllTextAsync(directory, "blocked");
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => scenario.ExecuteAsync());
        }
        finally
        {
            File.Delete(directory);
        }
    }
}
