using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class BackupRestoreView : UserControl
{
    public BackupRestoreView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is BackupRestoreViewModel vm)
        {
            vm.RequestBrowseFile = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { Title = "Select backup file", AllowMultiple = false });

                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            _ = vm.InitializeAsync();
        }
    }
}
