using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class CsvImportWizardView : Window
{
    public CsvImportWizardView()
    {
        InitializeComponent();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is CsvImportWizardViewModel vm)
            vm.SetTopLevel(TopLevel.GetTopLevel(this));
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
