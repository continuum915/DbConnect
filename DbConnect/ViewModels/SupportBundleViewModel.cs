using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SupportBundleViewModel : ViewModelBase
{
    public SupportBundleDraft Draft { get; }
    public ObservableCollection<SupportBundleEntry> Entries { get; }
    public Func<Task<bool>>? SaveRequested { get; set; }
    public bool Saved { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    private SupportBundleEntry? _selectedEntry;

    public string SelectedContent => SelectedEntry?.Content ?? string.Empty;

    public SupportBundleViewModel(SupportBundleDraft draft)
    {
        Draft = draft;
        Entries = new ObservableCollection<SupportBundleEntry>(draft.Entries);
        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
    }

    partial void OnSelectedEntryChanged(SupportBundleEntry? value) =>
        OnPropertyChanged(nameof(SelectedContent));

    public async Task<bool> SaveAsync()
    {
        try
        {
            Saved = SaveRequested != null && await SaveRequested();
            if (!Saved && string.IsNullOrEmpty(StatusMessage))
                StatusMessage = "Choose a destination to save the bundle.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {DiagnosticRedactor.Redact(ex.Message)}";
            Saved = false;
        }

        OnPropertyChanged(nameof(StatusMessage));
        return Saved;
    }
}
