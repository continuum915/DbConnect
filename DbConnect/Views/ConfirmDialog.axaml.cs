using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

/// <summary>A two-button confirmation. The safe choice (cancel) is the default; it closes with false.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string confirmText, string cancelText) : this()
    {
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
