using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class ExportDataDialog : Window
{
    public ExportDataDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
