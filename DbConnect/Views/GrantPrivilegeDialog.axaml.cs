using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class GrantPrivilegeDialog : Window
{
    public string? Privilege { get; private set; }
    public string? ObjectName { get; private set; }

    private readonly Func<string, string?, string> _previewGenerator;

    public GrantPrivilegeDialog() : this(string.Empty, Array.Empty<string>(), (_, _) => string.Empty)
    {
    }

    public GrantPrivilegeDialog(string username, IEnumerable<string> privileges, Func<string, string?, string> previewGenerator)
    {
        InitializeComponent();
        Title = $"Grant Privilege — {username}";
        _previewGenerator = previewGenerator;
        var combo = this.FindControl<ComboBox>("PrivilegeBox")!;
        combo.ItemsSource = privileges.ToList();
        combo.SelectedIndex = combo.ItemCount > 0 ? 0 : -1;
        combo.SelectionChanged += (_, _) => UpdatePreview();
        this.FindControl<TextBox>("ObjectBox")!.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var combo = this.FindControl<ComboBox>("PrivilegeBox")!;
        var privilege = combo.SelectedItem?.ToString() ?? string.Empty;
        var objectName = this.FindControl<TextBox>("ObjectBox")!.Text?.Trim();
        this.FindControl<TextBlock>("PreviewText")!.Text =
            privilege.Length > 0 ? _previewGenerator(privilege, string.IsNullOrEmpty(objectName) ? null : objectName) : string.Empty;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Grant_Click(object? sender, RoutedEventArgs e)
    {
        var combo = this.FindControl<ComboBox>("PrivilegeBox")!;
        Privilege = combo.SelectedItem?.ToString();
        var objectName = this.FindControl<TextBox>("ObjectBox")!.Text?.Trim();
        ObjectName = string.IsNullOrEmpty(objectName) ? null : objectName;
        if (string.IsNullOrEmpty(Privilege))
            return;

        Close(this);
    }
}
