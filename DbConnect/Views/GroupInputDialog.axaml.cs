using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class GroupInputDialog : Window
{
    public GroupInputDialog()
    {
        InitializeComponent();
    }

    public GroupInputDialog(string prompt, string initialValue = "") : this()
    {
        this.FindControl<TextBlock>("PromptLabel")!.Text = prompt;
        this.FindControl<TextBox>("InputBox")!.Text = initialValue;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) =>
        Close(this.FindControl<TextBox>("InputBox")!.Text?.Trim());

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
