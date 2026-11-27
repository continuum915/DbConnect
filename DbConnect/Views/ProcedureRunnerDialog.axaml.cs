using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ProcedureRunnerDialog : Window
{
    public ProcedureRunnerDialog()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is ProcedureRunnerViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
