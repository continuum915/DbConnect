using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>UI redesign 09: the connection dialog's tabs, footer and provider change.</summary>
public class ConnectionDialogRedesignTests
{
    [AvaloniaTheory]
    [InlineData("SQLite", new[] { "General", "Advanced" })]
    [InlineData("DuckDB", new[] { "General", "Advanced" })]
    [InlineData("PostgreSQL", new[] { "General", "Advanced", "Databases", "SSL", "SSH", "HTTP" })]
    [InlineData("MySQL", new[] { "General", "Advanced", "Databases", "SSL", "SSH", "HTTP" })]
    public void FileDatabases_ShowOnlyGeneralAndAdvanced(string provider, string[] tabs)
    {
        var vm = new ConnectionDialogViewModel { SelectedProvider = provider };
        var dialog = new ConnectionDialog { DataContext = vm };
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var shown = dialog.FindControl<TabControl>("ConnectionTabs")!.Items.OfType<TabItem>()
                .Where(t => t.IsVisible).Select(t => t.Header?.ToString());
            Assert.Equal(tabs, shown);
            Assert.Equal(provider is "SQLite" or "DuckDB", dialog.FindControl<TextBox>("DatabaseFileBox")!.IsEffectivelyVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ChangingProvider_KeepsTheNameHostAndUser()
    {
        var vm = new ConnectionDialogViewModel { SelectedProvider = "PostgreSQL" };
        vm.ConnectionName = "orders";
        vm.Host = "db.example.com";
        vm.Username = "rain";

        vm.SelectedProvider = "MySQL";

        Assert.Equal("orders", vm.ConnectionName);
        Assert.Equal("db.example.com", vm.Host);
        Assert.Equal("rain", vm.Username);
    }

    [AvaloniaFact]
    public void ConnectIsTheDefault_CancelIsTheCancel_AndSshFieldsWaitForTheTunnel()
    {
        var vm = new ConnectionDialogViewModel { SelectedProvider = "PostgreSQL" };
        var dialog = new ConnectionDialog { DataContext = vm };
        try
        {
            dialog.Show();
            var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToList();
            Assert.Equal("Connect", Assert.Single(buttons, b => b.IsDefault).Content);
            Assert.Equal("Cancel", Assert.Single(buttons, b => b.IsCancel).Content);
            Assert.Contains(buttons, b => Equals(b.Content, "Save"));
            Assert.Contains(buttons, b => Equals(b.Content, "Test Connection"));

            var sshForm = dialog.GetLogicalDescendants().OfType<Grid>()
                .Single(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "SSH host:"));
            Assert.False(sshForm.IsEnabled);
            vm.UseSshTunnel = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(sshForm.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }
}
