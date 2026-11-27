using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The Data Transfer wizard's saved profiles: Load on the Source step fills every step from a profile (its connections
/// must be open), and Save on the Options step keeps the current transfer under a name, which Automation can run.
/// </summary>
public sealed partial class DataTransferProfilesViewModel(DataTransferViewModel wizard, DataTransferProfiles store) : ObservableObject
{
    public ObservableCollection<string> Names { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(DeleteCommand))]
    private string? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _newName = string.Empty;

    [ObservableProperty] private string _message = string.Empty;

    public async Task RefreshAsync()
    {
        var selected = Selected;
        Names.Clear();
        foreach (var profile in await store.LoadAsync()) Names.Add(profile.Name);
        Selected = Names.FirstOrDefault(n => n == selected);
    }

    private bool HasSelection() => Selected != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task LoadAsync()
    {
        if (await store.FindAsync(Selected!) is not { } profile)
        {
            Message = $"The profile \"{Selected}\" no longer exists.";
            await RefreshAsync();
            return;
        }
        var error = await wizard.ApplyProfileAsync(profile);
        Message = error ?? $"Loaded \"{profile.Name}\". Review the options, then go on to the preview.";
        if (error == null) NewName = profile.Name;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (wizard.ToProfile(NewName.Trim()) is not { } profile)
        {
            Message = "Choose the source, the target and the tables before saving a profile.";
            return;
        }
        try
        {
            await store.SaveAsync(profile);
            await RefreshAsync();
            Selected = profile.Name;
            Message = $"Saved \"{profile.Name}\". Automation can run it as a Data Transfer job.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Message = $"The profile could not be saved: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        await store.DeleteAsync(Selected!);
        Message = $"Deleted \"{Selected}\".";
        await RefreshAsync();
    }
}
