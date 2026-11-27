using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class SupportBundleDialog : Window
{
    public SupportBundleDialog() => InitializeComponent();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SupportBundleViewModel viewModel && await viewModel.SaveAsync())
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
