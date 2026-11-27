using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class QueryParamDialog : Window
{
    public QueryParamDialog()
    {
        InitializeComponent();
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is QueryParamDialogViewModel vm)
        {
            Close(vm.GetResult(true));
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is QueryParamDialogViewModel vm)
        {
            Close(vm.GetResult(false));
        }
    }
}
