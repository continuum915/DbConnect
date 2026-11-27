using System.Diagnostics;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class AppPersistenceIsolationTests
{
    [Theory]
    [InlineData("isolated")]
    [InlineData("default")]
    [InlineData("invalid")]
    [InlineData("recovery-content")]
    public async Task FreshProcessUsesAssignedDirectoryForDefaultPersistence(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // A literal persisted document checks compatibility without using the writer as its own oracle.
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"ThemeVariant\":\"Dark\",\"WindowWidth\":1375,\"WindowX\":0,\"WindowY\":0}");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("vstest");
            startInfo.ArgumentList.Add(typeof(AppPersistenceIsolationTests).Assembly.Location);
            startInfo.ArgumentList.Add(mode switch
            {
                "isolated" => "/TestCaseFilter:FullyQualifiedName~DbConnect.Tests.IsolatedPersistenceProbe",
                "recovery-content" => "/TestCaseFilter:FullyQualifiedName~DbConnect.Tests.ViewModelRecoveryContentProbe",
                _ => "/TestCaseFilter:FullyQualifiedName~DbConnect.Tests.IsolatedPersistenceProbe.DirectoryResolution"
            });
            startInfo.Environment.Remove("DBCONNECT_DATA_DIRECTORY");
            if (mode != "default")
                startInfo.Environment["DBCONNECT_DATA_DIRECTORY"] = mode == "invalid" ? "relative-path" : directory;
            startInfo.Environment["DBCONNECT_PERSISTENCE_PROBE"] = mode == "recovery-content" ? "isolated" : mode;
            startInfo.Environment["DBCONNECT_PERSISTENCE_PROBE_DIRECTORY"] = directory;
            if (mode == "recovery-content")
                startInfo.Environment["DBCONNECT_RECOVERY_CONTENT_PROBE"] = "1";
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
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
            Assert.True(File.Exists(Path.Combine(directory, "probe-completed")), await stdout);
            if (mode == "isolated")
                Assert.True(File.Exists(Path.Combine(directory, "lifecycle-completed")), await stdout);
            if (mode == "recovery-content")
            {
                // The owning process has exited, so the public recovery reader now sees its actual timer-written snapshot.
                var recovery = new SessionRecoveryService(Path.Combine(directory, "recovery"));
                var orphan = Assert.Single(await recovery.ReadOrphanedSnapshotsAsync());
                Assert.Equal("SELECT recovery_content_probe", Assert.Single(orphan.Snapshot.Tabs).QueryText);
                await recovery.DeleteSnapshotAsync(orphan.FilePath);
                Assert.Empty(await recovery.ReadOrphanedSnapshotsAsync());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class IsolatedPersistenceProbe
{
    [Fact]
    public async Task DefaultServicesRoundTripOnlyAssignedStorage()
    {
        // Only the supervised process runs this probe; the normal test host never changes its profile.
        if (Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE") != "isolated")
            return;

        var directory = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY")!;
        Assert.Equal(directory, AppDataPaths.RootDirectory);
        Assert.Equal(Path.Combine(directory, "exports"), AppDataPaths.DesktopExportDirectory);
        try
        {
            Environment.SetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY", Path.Combine(directory, "late-change"));
            Assert.Equal(directory, AppDataPaths.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY", directory);
        }
        var sshKey = new SshHostKey("isolated-bastion.example", 2222, "ssh-ed25519", "SHA256:isolated-key");
        Assert.True(new KnownHostsStore().Save(sshKey, null));
        Assert.Equal(sshKey, new KnownHostsStore().Find("isolated-bastion.example", 2222));
        Assert.True(File.Exists(Path.Combine(directory, "ssh-known-hosts.json")));

        var settings = await new SettingsService().LoadAsync();
        Assert.Equal("Dark", settings.ThemeVariant);
        Assert.Equal(1375, settings.WindowWidth);
        settings.WindowWidth = 1450;
        await new SettingsService().SaveAsync(settings);
        Assert.Equal(1450, (await new SettingsService().LoadAsync()).WindowWidth);

        var diagnostics = DiagnosticsService.Default;
        Assert.Equal(Path.Combine(directory, "diagnostics"), diagnostics.RootDirectory);
        diagnostics.Record(diagnostics.BeginOperation("test", "isolated"), DiagnosticSeverity.Information, "probe");
        Assert.True(File.Exists(Path.Combine(directory, "diagnostics", "events.jsonl")));

        var workspace = new QueryWorkspaceService();
        var connection = new ConnectionInfo { Name = "synthetic", Provider = "SQLite", ConnectionString = "Data Source=:memory:" };
        await workspace.AddHistoryEntryAsync(connection, new QueryHistoryEntry { Sql = "SELECT 17" });
        Assert.Equal("SELECT 17", Assert.Single(await new QueryWorkspaceService().LoadHistoryAsync(connection)).Sql);
        Assert.True(File.Exists(Path.Combine(directory, "query-workspace.json")));

        var persistence = new SessionPersistenceService();
        await persistence.SaveAsync(new SavedSession { WasCleanShutdown = true, Tabs = [new SavedTab { QueryText = "SELECT 23" }] });
        Assert.Equal("SELECT 23", Assert.Single((await new SessionPersistenceService().LoadAsync())!.Tabs).QueryText);
        Assert.True(File.Exists(Path.Combine(directory, "last-session.json")));

        var recovery = new SessionRecoveryService();
        await recovery.WriteSnapshotAsync(new SessionSnapshot { Pid = int.MaxValue, Timestamp = DateTimeOffset.UtcNow, Tabs = [new TabSnapshot { QueryText = "SELECT 29" }] });
        var orphan = Assert.Single(await new SessionRecoveryService().ReadOrphanedSnapshotsAsync());
        Assert.Equal("SELECT 29", Assert.Single(orphan.Snapshot.Tabs).QueryText);
        Assert.StartsWith(Path.Combine(directory, "recovery"), orphan.FilePath);
        await recovery.DeleteCurrentSnapshotAsync();
        Assert.Empty(await new SessionRecoveryService().ReadOrphanedSnapshotsAsync());

        await new SnippetService().SaveAsync(new Snippet { Name = "isolated", Body = "SELECT 31" });
        Assert.Equal("SELECT 31", Assert.Single(await new SnippetService().LoadAllAsync()).Body);
        Assert.True(File.Exists(Path.Combine(directory, "snippets", "snippets.global.json")));

        var credentials = EncryptedFileStore.CreateForCurrentPlatform();
        await credentials.SetAsync("probe", "synthetic-secret");
        Assert.Equal("synthetic-secret", await EncryptedFileStore.CreateForCurrentPlatform().GetAsync("probe"));
        Assert.True(File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "secrets.dpapi.json" : "vault.json")));
        await credentials.DeleteAsync("probe");
        Assert.Empty(await new ConnectionSettingsService().LoadConnectionsAsync());

        await new AuditLogService().AppendAsync(new AuditEntry { ConnectionName = "probe", QueryText = "SELECT 47" });
        Assert.Equal("SELECT 47", Assert.Single(await new AuditLogService().LoadEntriesAsync("probe")).QueryText);
        Assert.True(File.Exists(Path.Combine(directory, "audit", "audit-probe.jsonl")));

        await SlowQueryLogService.Default.AppendAsync(new SlowQueryEntry { ConnectionName = "probe", QueryText = "SELECT 53" });
        Assert.Equal("SELECT 53", Assert.Single(await new SlowQueryLogService().LoadEntriesAsync("probe")).QueryText);
        Assert.True(File.Exists(Path.Combine(directory, "slow-queries-probe.jsonl")));

        var context = new QueryParamContext { Name = "isolated parameters" };
        await new QueryParamContextService().SaveAsync(context);
        Assert.Equal(context.Name, Assert.Single(await new QueryParamContextService().LoadAsync()).Name);
        Assert.True(File.Exists(Path.Combine(directory, "paramcontexts", $"{context.Id}.json")));

        await new DiagramWorkspaceService().SaveAsync(new DiagramWorkspace { ConnectionName = "probe", DatabaseName = "synthetic" });
        Assert.NotNull(await new DiagramWorkspaceService().LoadAsync("probe", "synthetic"));
        Assert.True(File.Exists(Path.Combine(directory, "diagrams", "probe_synthetic.dbcdiagram")));

        await new AutomationService().SaveJobAsync(new AutomationJob { Name = "disabled probe", IsEnabled = false });
        Assert.Equal("disabled probe", Assert.Single(await new AutomationService().LoadJobsAsync()).Name);
        Assert.True(File.Exists(Path.Combine(directory, "automation-jobs.json")));

        await new BackupService().SaveProfileAsync(new BackupProfile { Name = "isolated backup" });
        Assert.Equal("isolated backup", Assert.Single(await new BackupService().LoadProfilesAsync()).Name);
        Assert.True(File.Exists(Path.Combine(directory, "backup-profiles.json")));

        Assert.Empty(await new SchemaSnapshotService().ListSnapshotsAsync("probe"));
        Assert.True(Directory.Exists(Path.Combine(directory, "snapshots", "probe")));

        await File.WriteAllTextAsync(Path.Combine(directory, "probe-completed"), "ok");
    }

    [Fact]
    public async Task DirectoryResolutionPreservesDefaultsAndRejectsRelativeOverrides()
    {
        var mode = Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE");
        if (mode is not ("default" or "invalid"))
            return;

        if (mode == "invalid")
            Assert.Throws<ArgumentException>(() => AppDataPaths.RootDirectory);
        else
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DbConnect"), AppDataPaths.RootDirectory);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), AppDataPaths.DesktopExportDirectory);
        }

        await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE_DIRECTORY")!,
            "probe-completed"), "ok");
    }

    [AvaloniaFact]
    public async Task DesktopStartupAndCloseUseIsolatedSettingsRecoveryAndSession()
    {
        if (Environment.GetEnvironmentVariable("DBCONNECT_PERSISTENCE_PROBE") != "isolated")
            return;

        var directory = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY")!;
        await new SessionPersistenceService().SaveAsync(new SavedSession
        {
            WasCleanShutdown = true,
            Tabs = [new SavedTab { Title = "isolated tab", QueryText = "SELECT 41" }]
        });

        // Real App resources are bootstrapped by TestAppBuilder; attaching the view model
        // runs MainWindow's normal initialization, session restoration, and close handlers.
        var vm = new MainWindowViewModel(new DatabaseService(), new QueryWorkspaceService(), new SettingsService());
        var window = new DbConnect.Views.MainWindow { DataContext = vm };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (vm.SelectedTheme != "Dark" || vm.Tabs.OfType<QueryTabViewModel>().SingleOrDefault()?.QueryText != "SELECT 41")
                await Task.Delay(20, timeout.Token);

            Assert.Empty(vm.ActiveSessionOptions);
            Assert.Empty(vm.SchemaNodes);
            var tab = Assert.IsType<QueryTabViewModel>(Assert.Single(vm.Tabs));
            Assert.False(tab.HasDbSession);
            tab.QueryText = "SELECT 43";

            // Only unsaved text is snapshotted for crash recovery.
            var recoveryDirectory = Path.Combine(directory, "recovery");
            while (!Directory.Exists(recoveryDirectory) || Directory.GetFiles(recoveryDirectory, "session-*.json").Length == 0)
                await Task.Delay(20, timeout.Token);

            // Quitting with unsaved text now asks Save / Don't Save / Cancel; this probe checks
            // persistence, not prompting, so treat the edit as saved before closing.
            tab.ClearDirty();

            window.Close();
            await closed.Task.WaitAsync(timeout.Token);
            var saved = await new SessionPersistenceService().LoadAsync();
            Assert.True(saved!.WasCleanShutdown);
            Assert.Equal("SELECT 43", Assert.Single(saved.Tabs).QueryText);
            Assert.Empty(Directory.GetFiles(recoveryDirectory, "session-*.json"));
            Assert.Equal("Dark", (await new SettingsService().LoadAsync()).ThemeVariant);
            await File.WriteAllTextAsync(Path.Combine(directory, "lifecycle-completed"), "ok");
        }
        finally
        {
            await vm.OnCleanExitAsync();
            if (window.IsVisible)
                window.Close();
        }
    }
}
