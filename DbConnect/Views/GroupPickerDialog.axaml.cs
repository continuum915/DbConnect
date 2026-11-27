using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class GroupPickerDialog : Window
{
    public GroupPickerDialog()
    {
        InitializeComponent();
    }

    public GroupPickerDialog(IEnumerable<string> groups) : this()
    {
        var combo = this.FindControl<ComboBox>("GroupCombo")!;
        combo.ItemsSource = groups.ToList();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) =>
        Close(this.FindControl<ComboBox>("GroupCombo")!.Text?.Trim() ?? string.Empty);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
