using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Views;

namespace DbConnect.ViewModels;

/// <summary>Opens the Preferences dialog over the settings in effect.</summary>
public sealed partial class PreferencesLauncher(Func<PreferencesViewModel> create, Func<Window, Task> showDialog)
{
    [RelayCommand]
    private async Task Open()
    {
        var preferences = create();
        await preferences.LoadAsync();
        await showDialog(new PreferencesDialog { DataContext = preferences });
    }
}
