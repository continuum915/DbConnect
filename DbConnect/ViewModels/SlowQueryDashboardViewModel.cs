using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SlowQueryDashboardViewModel : ViewModelBase
{
    private readonly SlowQueryLogService _logService;
    private readonly QueryWorkspaceService? _workspaceService;

    public SlowQueryDashboardViewModel(
        ObservableCollection<DatabaseSession> availableSessions,
        SlowQueryLogService? logService = null,
        QueryWorkspaceService? workspaceService = null)
    {
        Title = "Slow Query Dashboard";
        _logService = logService ?? SlowQueryLogService.Default;
        _workspaceService = workspaceService;

        foreach (var s in availableSessions)
            AvailableSessions.Add(s);

        SelectedSession = AvailableSessions.FirstOrDefault();
    }

    public SlowQueryDashboardViewModel() : this(new ObservableCollection<DatabaseSession>()) { }

    // ── Properties ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<DatabaseSession> _availableSessions = new();

    [ObservableProperty]
    private DatabaseSession? _selectedSession;

    [ObservableProperty]
    private ObservableCollection<SlowQueryAggregate> _aggregates = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredAggregates))]
    private SlowQueryAggregate? _selectedAggregate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredAggregates))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private int _thresholdMs;

    [ObservableProperty]
    private string _statusMessage = "Select a session and click Load.";

    public ObservableCollection<SlowQueryAggregate> FilteredAggregates
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilterText))
                return Aggregates;

            var filtered = new ObservableCollection<SlowQueryAggregate>(
                Aggregates.Where(a =>
                    a.Fingerprint.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                    a.SampleQuery.Contains(FilterText, StringComparison.OrdinalIgnoreCase)));
            return filtered;
        }
    }

    /// <summary>Raised when a new query tab should be opened with the given SQL.</summary>
    public Action<string>? RequestOpenInTab { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (SelectedSession == null)
        {
            StatusMessage = "Select a session first.";
            return;
        }

        try
        {
            var aggregates = await _logService.GetAggregatesAsync(SelectedSession.Name);
            Aggregates.Clear();
            foreach (var a in aggregates)
                Aggregates.Add(a);
            OnPropertyChanged(nameof(FilteredAggregates));
            StatusMessage = $"Loaded {Aggregates.Count} query pattern(s) for {SelectedSession.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearLogAsync()
    {
        if (SelectedSession == null) return;
        await _logService.ClearAsync(SelectedSession.Name);
        Aggregates.Clear();
        OnPropertyChanged(nameof(FilteredAggregates));
        StatusMessage = "Log cleared.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Aggregates.Count == 0)
        {
            StatusMessage = "Nothing to export.";
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Fingerprint,Count,AvgMs,MaxMs,LastSeen,Connection,SampleQuery");
            foreach (var a in FilteredAggregates)
            {
                sb.AppendLine(
                    $"\"{Escape(a.Fingerprint)}\",{a.Count}," +
                    $"{a.AvgDuration.TotalMilliseconds:F0},{a.MaxDuration.TotalMilliseconds:F0}," +
                    $"{a.LastSeen:o},\"{Escape(a.ConnectionName)}\",\"{Escape(a.SampleQuery)}\"");
            }

            Directory.CreateDirectory(AppDataPaths.DesktopExportDirectory);
            var path = Path.Combine(
                AppDataPaths.DesktopExportDirectory,
                $"slow-queries-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(path, sb.ToString());
            StatusMessage = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanViewPlan))]
    private void ViewPlan()
    {
        if (!CanViewPlan()) return;
        var explainSql = $"EXPLAIN\n{SelectedAggregate!.SampleQuery}";
        RequestOpenInTab?.Invoke(explainSql);
    }

    // A truncated sample is not the query that ran, and may stop partway through a statement.
    private bool CanViewPlan() => SelectedAggregate is { IsTruncated: false };

    partial void OnSelectedAggregateChanged(SlowQueryAggregate? value)
    {
        ViewPlanCommand.NotifyCanExecuteChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Escape(string s) => s.Replace("\"", "\"\"");
}
