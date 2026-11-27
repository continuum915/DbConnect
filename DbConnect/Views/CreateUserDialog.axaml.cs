using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class CreateUserDialog : Window
{
    public string? Username { get; private set; }
    public string? Password { get; private set; }

    private readonly Func<string, string, string> _previewGenerator;

    public CreateUserDialog() : this((_, _) => string.Empty)
    {
    }

    public CreateUserDialog(Func<string, string, string> previewGenerator)
    {
        InitializeComponent();
        _previewGenerator = previewGenerator;
        this.FindControl<TextBox>("UsernameBox")!.TextChanged += (_, _) => UpdatePreview();
        this.FindControl<TextBox>("PasswordBox")!.TextChanged += (_, _) => UpdatePreview();
    }

    private void UpdatePreview()
    {
        var username = this.FindControl<TextBox>("UsernameBox")!.Text ?? string.Empty;
        var password = this.FindControl<TextBox>("PasswordBox")!.Text ?? string.Empty;
        this.FindControl<TextBlock>("PreviewText")!.Text =
            username.Length > 0 ? _previewGenerator(username, password.Length > 0 ? "•••" : string.Empty) : string.Empty;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        Username = this.FindControl<TextBox>("UsernameBox")!.Text?.Trim();
        Password = this.FindControl<TextBox>("PasswordBox")!.Text;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
            return;

        Close(this);
    }
}
