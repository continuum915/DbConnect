using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class IndexManagerView : UserControl
{
    public IndexManagerView()
    {
        InitializeComponent();
    }

    private void OnColumnToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is IndexManagerViewModel vm)
            vm.NotifyColumnSelectionChanged();
    }

    private void OnDirectionToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is IndexColumnEntry entry)
        {
            entry.ToggleDirection();
            if (this.DataContext is IndexManagerViewModel vm)
                vm.NotifyColumnSelectionChanged();
        }
    }
}
