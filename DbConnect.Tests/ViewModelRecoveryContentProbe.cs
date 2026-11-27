using Avalonia.Headless.XUnit;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>A supervised unclean exit lets the parent read an actual view-model recovery snapshot.</summary>
public class ViewModelRecoveryContentProbe
{
    [AvaloniaFact]
    public async Task UncleanHostLeavesItsTimerWrittenSnapshotForRecovery()
    {
        if (Environment.GetEnvironmentVariable("DBCONNECT_RECOVERY_CONTENT_PROBE") != "1") return;
        var directory = Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE_DIRECTORY")!;
        var viewModel = new MainWindowViewModel(new DatabaseService());
        viewModel.NewQueryTabWithSession().QueryText = "SELECT recovery_content_probe";
        var recoveryDirectory = Path.Combine(directory, "recovery");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!SnapshotWriteHasCompleted(recoveryDirectory))
            await Task.Delay(20, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "probe-completed"), "ok");
        GC.KeepAlive(viewModel);
        // Intentionally omit clean exit: the supervised parent validates the orphan and owns cleanup.
    }

    private static bool SnapshotWriteHasCompleted(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var path in Directory.GetFiles(directory, "session-*.json"))
        {
            try
            {
                // The writer's stream must close before this exclusive read can open.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (stream.Length > 0) return true;
            }
            catch (IOException) { }
        }
        return false;
    }
}
