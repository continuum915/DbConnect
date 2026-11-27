using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class ChangePasswordDialog : Window
{
    public string Username { get; }
    public string? Password { get; private set; }

    private readonly Func<string, string, string> _previewGenerator;

    public ChangePasswordDialog() : this(string.Empty, (_, _) => string.Empty)
    {
    }

    public ChangePasswordDialog(string username, Func<string, string, string> previewGenerator)
    {
        InitializeComponent();
        Username = username;
        _previewGenerator = previewGenerator;
        this.FindControl<TextBlock>("UsernameText")!.Text = username;
        this.FindControl<TextBox>("PasswordBox")!.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var password = this.FindControl<TextBox>("PasswordBox")!.Text ?? string.Empty;
        this.FindControl<TextBlock>("PreviewText")!.Text =
            password.Length > 0 ? _previewGenerator(Username, "•••") : string.Empty;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        Password = this.FindControl<TextBox>("PasswordBox")!.Text;
        if (string.IsNullOrEmpty(Password))
            return;

        Close(this);
    }
}
