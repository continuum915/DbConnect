using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class AuditLogViewModel : ViewModelBase
{
    private readonly AuditLogService _svc = new();

    [ObservableProperty] private DatabaseSession? _selectedSession;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private DateTimeOffset? _filterFrom;
    [ObservableProperty] private DateTimeOffset? _filterTo;
    [ObservableProperty] private bool _showErrorsOnly;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private AuditEntry? _selectedEntry;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<AuditEntry> Entries { get; } = new();

    public Action<string>? RequestOpenInTab { get; set; }

    public AuditLogViewModel(ObservableCollection<DatabaseSession> sessions)
    {
        Title = "Audit Log";
        AvailableSessions = sessions;
        SelectedSession = sessions.FirstOrDefault();
    }

    public AuditLogViewModel() : this(new ObservableCollection<DatabaseSession>()) { }

    [RelayCommand]
    private async Task Search()
    {
        if (SelectedSession == null)
        {
            StatusMessage = "Select a session first.";
            return;
        }
        try
        {
            var results = await _svc.SearchAsync(
                SelectedSession.Name,
                string.IsNullOrWhiteSpace(FilterText) ? null : FilterText,
                FilterFrom,
                FilterTo,
                ShowErrorsOnly ? true : null);
            Entries.Clear();
            foreach (var e in results) Entries.Add(e);
            StatusMessage = $"{Entries.Count} entries found.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearLog()
    {
        if (SelectedSession == null) return;
        await _svc.ClearLogAsync(SelectedSession.Name);
        Entries.Clear();
        StatusMessage = "Log cleared.";
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (Entries.Count == 0) { StatusMessage = "No entries to export."; return; }
        try
        {
            var dir = Path.Combine(AppDataPaths.RootDirectory, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"audit-export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
            await _svc.ExportCsvAsync(Entries.ToList(), path);
            StatusMessage = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ReRun()
    {
        if (SelectedEntry == null) return;
        RequestOpenInTab?.Invoke(SelectedEntry.QueryText);
    }
}
