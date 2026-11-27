using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ConnectionMonitorViewModel : ViewModelBase
{
    private readonly ConnectionMonitorService _monitorService = new();
    private Timer? _autoRefreshTimer;

    public ConnectionMonitorViewModel()
    {
        Title = "Connection Monitor";
    }

    public ConnectionMonitorViewModel(DatabaseSession session) : this()
    {
        Session = session;
        CheckEmbeddedProvider();
    }

    // ── Properties ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private DatabaseSession? _session;

    [ObservableProperty]
    private ObservableCollection<ActiveQueryInfo> _activeQueries = new();

    [ObservableProperty]
    private ActiveQueryInfo? _selectedQuery;

    [ObservableProperty]
    private int _refreshIntervalSeconds = 15;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DateTimeOffset _lastRefreshed;

    [ObservableProperty]
    private bool _isEmbeddedProvider;

    public IReadOnlyList<int> RefreshIntervalChoices { get; } = [5, 15, 30, 0];

    /// <summary>Called by MainWindow to wire confirmation dialog.</summary>
    public Func<string, Task<bool>>? ShowConfirmDialog { get; set; }

    // ── Partial callbacks ─────────────────────────────────────────────────────

    partial void OnSessionChanged(DatabaseSession? value)
    {
        CheckEmbeddedProvider();
    }

    partial void OnRefreshIntervalSecondsChanged(int value)
    {
        if (value > 0)
            StartAutoRefresh();
        else
            StopAutoRefresh();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Session == null) return;
        IsRefreshing = true;
        StatusMessage = "Refreshing…";
        try
        {
            var queries = await _monitorService.GetActiveQueriesAsync(Session);
            Dispatcher.UIThread.Invoke(() =>
            {
                ActiveQueries.Clear();
                foreach (var q in queries)
                    ActiveQueries.Add(q);
                LastRefreshed = DateTimeOffset.Now;
                StatusMessage = $"Refreshed at {LastRefreshed:HH:mm:ss} — {ActiveQueries.Count} active query(ies).";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanKillQuery))]
    private async Task KillSelectedQueryAsync()
    {
        if (Session == null || SelectedQuery == null) return;

        var killStmt = await _monitorService.GetKillStatementAsync(Session, SelectedQuery.ConnectionId);

        bool confirmed = true;
        if (ShowConfirmDialog != null)
            confirmed = await ShowConfirmDialog($"Execute kill statement?\n\n{killStmt}");

        if (!confirmed) return;

        try
        {
            await Session.ExecuteNonQueryAsync(killStmt);
            StatusMessage = $"Killed connection {SelectedQuery.ConnectionId}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kill failed: {ex.Message}";
        }
    }

    private bool CanKillQuery() => SelectedQuery != null && Session != null;

    partial void OnSelectedQueryChanged(ActiveQueryInfo? value)
    {
        KillSelectedQueryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        if (RefreshIntervalSeconds <= 0) return;

        _autoRefreshTimer = new Timer(
            _ => _ = RefreshAsync(),
            null,
            TimeSpan.FromSeconds(RefreshIntervalSeconds),
            TimeSpan.FromSeconds(RefreshIntervalSeconds));
    }

    [RelayCommand]
    private void StopAutoRefresh()
    {
        _autoRefreshTimer?.Dispose();
        _autoRefreshTimer = null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void CheckEmbeddedProvider()
    {
        if (Session == null) return;
        IsEmbeddedProvider = Session.Capabilities.IsEmbedded;
        if (IsEmbeddedProvider)
            StatusMessage = "This provider has no active server connections to monitor.";
    }

    // Cleanup on disposal
    public void Dispose()
    {
        StopAutoRefresh();
    }
}
