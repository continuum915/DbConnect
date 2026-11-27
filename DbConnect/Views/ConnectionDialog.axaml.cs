using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.Models;
using DbConnect.ViewModels;
using Avalonia.Platform.Storage;

namespace DbConnect.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConnectionDialogViewModel vm)
            vm.TrustPrompt = (request, token) => SshTrustDialog.PromptAsync(this, request, token);
    }

    /// <summary>Closing the dialog cancels a test still in flight, so the test leaves nothing open.</summary>
    protected override void OnClosed(System.EventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm)
            vm.TestConnectionCancelCommand.Execute(null);
        base.OnClosed(e);
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm)
        {
            vm.SetResult();
            Close(vm.Result);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Select Database File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Database Files") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3", "*.duckdb" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        };

        var result = await StorageProvider.OpenFilePickerAsync(options);
        if (result != null && result.Count > 0 && DataContext is ConnectionDialogViewModel vm)
        {
            var filePath = result[0].Path.LocalPath;
            vm.DatabaseFilePath = filePath;
        }
    }
}
