using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

/// <summary>Progress, Cancel and outcome of a Dump SQL File run.</summary>
public partial class DumpSqlFileDialog : Window
{
    public DumpSqlFileDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
