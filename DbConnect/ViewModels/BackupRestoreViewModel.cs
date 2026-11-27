using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class BackupRestoreViewModel : ViewModelBase
{
    private readonly BackupService _svc = new();

    [ObservableProperty] private DatabaseSession? _targetSession;
    [ObservableProperty] private BackupProfile? _selectedProfile;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private bool _isBackupMode = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _pgDumpWarning = string.Empty;
    [ObservableProperty] private string _strategyDescription = string.Empty;
    [ObservableProperty] private string _restoreNote = string.Empty;
    [ObservableProperty] private bool _canCancel;
    private bool? _pgDumpFound;
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<BackupProfile> Profiles { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Wired by the View to open a file picker for the output path.</summary>
    public Func<Task<string?>>? RequestBrowseFile { get; set; }

    public BackupRestoreViewModel(ObservableCollection<DatabaseSession> availableSessions)
    {
        Title = "Backup / Restore";
        AvailableSessions = availableSessions;
    }

    public async Task InitializeAsync()
    {
        var profiles = await _svc.LoadProfilesAsync();
        Profiles.Clear();
        foreach (var p in profiles)
            Profiles.Add(p);

        _pgDumpFound = await _svc.CheckPgDumpAvailableAsync();
        OnTargetSessionChanged(TargetSession);
    }

    /// <summary>
    /// The dialog follows the selected session's backup strategy: what it writes, what a restore leaves after a failure,
    /// whether it can be cancelled, and the pg_dump warning.
    /// </summary>
    partial void OnTargetSessionChanged(DatabaseSession? value)
    {
        var strategy = value?.Capabilities.BackupStrategy;
        StrategyDescription = strategy?.Describe() ?? string.Empty;
        RestoreNote = value == null ? string.Empty : value.Capabilities.BackupStrategy.RestoreNote(value);
        CanCancel = strategy?.CanCancel() == true;
        PgDumpWarning = strategy == BackupStrategy.PostgreSqlTools && _pgDumpFound == false
            ? "⚠️ pg_dump not found in PATH. PostgreSQL backup/restore will not work."
            : string.Empty;
    }

    [RelayCommand]
    private Task StartBackup() =>
        RunAsync("Backup", "Select a session and output path before starting.",
            (session, progress, token) => _svc.BackupAsync(session, OutputPath, progress, token));

    [RelayCommand]
    private Task StartRestore() =>
        RunAsync("Restore", "Select a session and input path before restoring.",
            (session, progress, token) => _svc.RestoreAsync(session, OutputPath, progress, token));

    /// <summary>Stops a running backup or restore whose strategy can stop (<see cref="CanCancel"/>).</summary>
    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Runs a backup or restore with its log; a cancel or failure is logged as such, never as done.</summary>
    private async Task RunAsync(string operation, string missingInput,
        Func<DatabaseSession, IProgress<string>, CancellationToken, Task> run)
    {
        if (TargetSession == null || string.IsNullOrWhiteSpace(OutputPath))
        {
            LogLines.Add(missingInput);
            return;
        }

        IsRunning = true;
        LogLines.Clear();
        var progress = new Progress<string>(msg => LogLines.Add(msg));
        using var cancellation = _cancellation = new CancellationTokenSource();
        try
        {
            await run(TargetSession, progress, cancellation.Token);
        }
        catch (OperationCanceledException ex)
        {
            LogLines.Add($"⏹ {ex.Message}");
        }
        catch (Exception ex)
        {
            LogLines.Add($"❌ {operation} failed: {ex.Message}");
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputPath()
    {
        if (RequestBrowseFile != null)
        {
            var path = await RequestBrowseFile();
            if (!string.IsNullOrEmpty(path))
                OutputPath = path;
        }
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        if (TargetSession == null) return;

        var profile = SelectedProfile ?? new BackupProfile();
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = $"{TargetSession.Name} backup";
        profile.Provider = TargetSession.ProviderName;
        profile.OutputPath = OutputPath;

        await _svc.SaveProfileAsync(profile);
        await ReloadProfilesAsync();
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile == null) return;
        await _svc.DeleteProfileAsync(SelectedProfile.Id);
        await ReloadProfilesAsync();
        SelectedProfile = null;
    }

    private async Task ReloadProfilesAsync()
    {
        var profiles = await _svc.LoadProfilesAsync();
        Profiles.Clear();
        foreach (var p in profiles)
            Profiles.Add(p);
    }
}
