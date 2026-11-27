using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ForeignKeyLookupDialog : Window
{
    public ForeignKeyLookupDialog()
    {
        InitializeComponent();
    }

    public ForeignKeyLookupDialog(ForeignKeyLookupViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Chosen += value => Close(value);
        Opened += async (_, _) => await viewModel.LoadAsync();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
