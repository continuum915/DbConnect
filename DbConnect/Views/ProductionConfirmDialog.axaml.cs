using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ProductionConfirmDialog : Window
{
    public ProductionConfirmDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProductionConfirmViewModel vm)
        {
            vm.Confirm();
            Close();
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
