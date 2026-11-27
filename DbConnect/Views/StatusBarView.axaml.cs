using System.Reflection;
using Avalonia.Controls;

namespace DbConnect.Views;

public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
        if (Assembly.GetExecutingAssembly().GetName().Version is { } version)
            this.FindControl<TextBlock>("VersionLabel")!.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
