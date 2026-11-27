using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

public partial class AboutDialog : Window
{
    private const string RepoUrl = "https://github.com/continuum915/DbConnect";

    public AboutDialog()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        this.FindControl<TextBlock>("VersionLabel")!.Text =
            $"Version {ver?.ToString(3) ?? "unknown"}";

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("RepoLinkButton")!.Click += (_, _) => OpenUrl(RepoUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Browser launch failures are silently ignored
        }
    }
}
