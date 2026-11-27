using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class MainToolbarView : UserControl
{
    public MainToolbarView()
    {
        InitializeComponent();
    }

    /// <summary>Disconnects the connection selected in the navigator.</summary>
    private void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedSchemaNode: { } node } vm)
            vm.Disconnect(node);
    }
}
