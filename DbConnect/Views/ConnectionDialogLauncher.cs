using System.Threading.Tasks;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Opens the connection dialog over a window and connects to what the user saves.</summary>
internal static class ConnectionDialogLauncher
{
    public static async Task AddConnectionAsync(Window owner, MainWindowViewModel vm)
    {
        var dialogVm = new ConnectionDialogViewModel(new DatabaseService(), new ConnectionSettingsService());
        var dialog = new ConnectionDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<ConnectionInfo?>(owner);
        if (result != null)
            await vm.AddConnectionAsync(result, (request, token) => SshTrustDialog.PromptAsync(owner, request, token));
    }
}
